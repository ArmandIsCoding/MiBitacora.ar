"""Offline Apple Journal HTML importer. Standard library only; dry-run by default."""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import closing
from datetime import date, datetime, time, timedelta, timezone
import hashlib
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import unicodedata
from urllib.parse import unquote, urlsplit
import uuid

UUID = re.compile(r"\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", re.I)
MEDIA_TYPES = {".heic": "image/heic", ".heif": "image/heif", ".jpeg": "image/jpeg",
               ".jpg": "image/jpeg", ".png": "image/png", ".gif": "image/gif",
               ".webp": "image/webp", ".mov": "video/quicktime", ".mp4": "video/mp4",
               ".m4a": "audio/mp4", ".mp3": "audio/mpeg", ".wav": "audio/wav"}


def normalized(text):
    return "".join(c for c in unicodedata.normalize("NFD", text)
                   if unicodedata.category(c) != "Mn").lower()


def apple_filename(name):
    # Some Windows ZIP extractors decode Apple's UTF-8 filenames as legacy CP437.
    for encoding in ("cp437", "cp850"):
        try:
            name = name.encode(encoding).decode("utf-8")
            break
        except (UnicodeEncodeError, UnicodeDecodeError):
            pass
    return unicodedata.normalize("NFC", name)


def clean_text(parts):
    text = "".join(parts).replace("\xa0", " ")
    text = re.sub(r"[^\S\n]+", " ", text)
    text = re.sub(r" *\n *", "\n", text)
    return re.sub(r"\n{3,}", "\n\n", text).strip()


class EntryParser(HTMLParser):
    # Cocoa's HTML has divs inside p/span elements and unbalanced paragraph tags.
    # Events preserve the body text that a DOM repair can move out of bodyText.
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.section = None
        self.parts = {"title": [], "body": [], "assets": [], "date": []}
        self.references = []
        self.links = []
        self.body_count = 0
        self.ignored = 0

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag in ("script", "style"):
            self.ignored += 1
        classes = attrs.get("class", "").split()
        for css, section in (("pageHeader", "date"), ("assetGrid", "assets"),
                             ("title", "title"), ("bodyText", "body")):
            if css in classes:
                self.section = section
                if section == "body":
                    self.body_count += 1
        for key in ("src", "href", "poster"):
            if key in attrs and "Resources/" in attrs[key]:
                self.references.append(attrs[key])
        if self.section and tag in ("p", "div", "li", "br"):
            self.parts[self.section].append("\n" if tag == "br" else "\n\n")
            if tag == "li":
                self.parts[self.section].append("• ")
        if self.section == "body" and tag == "a":
            href = attrs.get("href", "")
            self.links.append(href if urlsplit(href).scheme in ("http", "https", "mailto") else "")

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.ignored = max(0, self.ignored - 1)
        if self.section == "body" and tag == "a" and self.links:
            href = self.links.pop()
            if href:
                self.parts["body"].append(f" ({href})")
        if self.section and tag in ("p", "div", "li", "h1", "h2", "blockquote"):
            self.parts[self.section].append("\n\n")
        if tag == "div" and self.section in ("title", "date"):
            self.section = None

    def handle_data(self, data):
        if self.section and not self.ignored:
            self.parts[self.section].append(data)


class IndexParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.entries = []

    def handle_starttag(self, tag, attrs):
        href = dict(attrs).get("href", "")
        if tag == "a" and href.startswith("Entries/"):
            self.entries.append(href)


def confined(root, path):
    root, path = root.resolve(), path.resolve()
    if not path.is_relative_to(root) or path == root:
        raise ValueError("Una ruta de la exportación sale de su carpeta autorizada.")
    return path


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def inspect_export(source):
    root = Path(source).resolve()
    if not (root / "index.html").is_file() and (root / "AppleJournalEntries" / "index.html").is_file():
        root /= "AppleJournalEntries"
    index = IndexParser()
    index.feed((root / "index.html").read_text(encoding="utf-8-sig"))
    entries_dir = root / "Entries"
    actual_paths = list(entries_dir.glob("*.html"))
    by_name = {apple_filename(path.name): path for path in actual_paths}
    if len(by_name) != len(actual_paths):
        raise ValueError("Hay nombres de entrada ambiguos tras normalizar la codificación.")
    paths = []
    for href in index.entries:
        relative = Path(unquote(href))
        if len(relative.parts) != 2 or relative.parts[0] != "Entries":
            raise ValueError("Una ruta del índice sale de la carpeta Entries.")
        matched = by_name.get(unicodedata.normalize("NFC", relative.name))
        if matched is None:
            raise ValueError("Falta una entrada del índice después de normalizar sus nombres.")
        paths.append(confined(entries_dir, matched))
    if not paths or len(paths) != len(set(paths)) or set(paths) != set(actual_paths):
        raise ValueError("El índice y los archivos de entradas no coinciden; se cancela la importación.")
    resources_dir = root / "Resources"
    resources = {file.name: confined(resources_dir, file) for file in resources_dir.iterdir() if file.is_file() and not file.name.startswith(".")}
    by_id = {}
    for name, path in resources.items():
        by_id.setdefault(path.stem.lower(), set()).add(name)
    used, entries = set(), []
    per_day = Counter()
    for path in reversed(paths):
        html = path.read_text(encoding="utf-8-sig")
        parser = EntryParser()
        parser.feed(html)
        if parser.body_count != 1:
            raise ValueError("Una entrada no tiene el formato bodyText esperado.")
        day = date.fromisoformat(path.name[:10])
        title = clean_text(parser.parts["title"])
        body = clean_text(parser.parts["body"])
        assets = clean_text(parser.parts["assets"])
        text = "\n\n".join(part for part in (title, body) if part)
        if assets:
            text += "\n\nRecursos de la entrada en Diario:\n" + assets
        associated, associated_names = [], set()

        def associate(names):
            for name in sorted(names):
                if name not in associated_names:
                    associated.append(name)
                    associated_names.add(name)

        for identifier in UUID.findall(html):
            associate(by_id.get(identifier.lower(), set()))
        for reference in parser.references:
            if urlsplit(reference).scheme or urlsplit(reference).netloc:
                raise ValueError("La exportación referencia un recurso remoto; no se descargará.")
            file = confined(resources_dir, path.parent / unquote(urlsplit(reference).path))
            if not file.is_file():
                raise ValueError("Falta un recurso referenciado por una entrada.")
            associate([file.name])
            associate(by_id.get(file.stem.lower(), set()))
        # Apple can link companion assets through metadata, including Live Photos.
        cursor = 0
        while cursor < len(associated):
            name = associated[cursor]
            cursor += 1
            if resources[name].suffix.lower() == ".json":
                metadata = resources[name].read_text(encoding="utf-8-sig")
                json.loads(metadata)  # Do not silently archive malformed metadata.
                for identifier in UUID.findall(metadata):
                    associate(by_id.get(identifier.lower(), set()))
        used.update(associated)
        media = [resources[name] for name in associated if resources[name].suffix.lower() != ".json"]
        # The export gives a calendar date, not an entry time. Noon -03:00 is only
        # an internal ordering anchor; IsDateOnly prevents displaying a made-up time.
        moment = datetime.combine(day, time(15), timezone.utc) + timedelta(microseconds=per_day[day])
        per_day[day] += 1
        entries.append({"source": path.relative_to(root).as_posix(), "date": day.isoformat(),
                        "createdUtc": moment.strftime("%Y-%m-%d %H:%M:%S.%f"), "text": text.strip(),
                        "title": title, "body": body, "media": media})
    unused = set(resources) - used
    unassigned_media = [name for name in unused if resources[name].suffix.lower() != ".json"]
    return {"root": root, "entries": entries, "resources": resources, "unused": unused,
            "unassignedMedia": unassigned_media}


def summary(plan):
    entries = plan["entries"]
    return {"entries": len(entries), "from": min(e["date"] for e in entries), "to": max(e["date"] for e in entries),
            "attachments": sum(len(e["media"]) for e in entries),
            "attachmentBytes": sum(file.stat().st_size for e in entries for file in e["media"]),
            "resourceFiles": len(plan["resources"]), "unassignedMedia": len(plan["unassignedMedia"]),
            "metadataFiles": sum(file.suffix.lower() == ".json" for file in plan["resources"].values()),
            "unassociatedResources": len(plan["unused"]),
            "largestTextCharacters": max(len(e["text"]) for e in entries),
            "mediaTypes": dict(Counter(file.suffix.lower() for e in entries for file in e["media"]))}


def replace_journal(plan, database, uploads, account, backups):
    if str(uuid.UUID(account)) != account:
        raise ValueError("El código de cuenta debe ser un UUID canónico.")
    if plan["unassignedMedia"]:
        raise ValueError("Hay archivos multimedia sin una entrada identificada. No se modificó la base.")
    database, uploads, backups = Path(database).resolve(), Path(uploads).resolve(), Path(backups).resolve()
    if not database.is_file():
        raise ValueError("La base de destino no existe.")
    account_dir = confined(uploads, uploads / account)
    if (uploads / account).is_symlink():
        raise ValueError("La carpeta de cuenta no puede ser un enlace simbólico.")
    db = sqlite3.connect(database, timeout=5, isolation_level=None)
    db.execute("PRAGMA foreign_keys=ON")
    copied, committed = [], False
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    backup_dir = backups / stamp
    try:
        if not db.execute("SELECT 1 FROM AspNetUsers WHERE Id=?", (account,)).fetchone():
            raise ValueError("No existe la cuenta de destino.")
        columns = {row[1] for row in db.execute("PRAGMA table_info(JournalEntries)")}
        if "IsDateOnly" not in columns:
            raise ValueError("Primero actualizá la aplicación y sus migraciones.")
        if "Content" in {row[1] for row in db.execute("PRAGMA table_info(JournalAttachments)")}:
            raise ValueError("La base todavía guarda BLOBs; primero migrá los uploads.")
        backup_dir.mkdir(parents=True, exist_ok=False)
        # SQLite's backup API includes committed WAL pages; copying only .db would not.
        with closing(sqlite3.connect(backup_dir / "mi-bitacora.db")) as backup_db:
            db.backup(backup_db)
        if uploads.exists():
            shutil.copytree(uploads, backup_dir / "uploads")
        shutil.copytree(plan["root"], backup_dir / "apple-export")
        old_ids = [row[0] for row in db.execute("SELECT a.Id FROM JournalAttachments a JOIN JournalEntries e ON e.Id=a.EntryId WHERE e.UserId=?", (account,))]
        old_entries = db.execute("SELECT COUNT(*) FROM JournalEntries WHERE UserId=?", (account,)).fetchone()[0]
        old_notes = db.execute("SELECT COUNT(*) FROM JournalNotes WHERE UserId=?", (account,)).fetchone()[0]
        account_dir.mkdir(parents=True, exist_ok=True)
        manifest = {"account": account, "summary": summary(plan), "replacedEntries": old_entries,
                    "replacedNotes": old_notes, "entries": []}
        db.execute("BEGIN IMMEDIATE")
        db.execute("DELETE FROM JournalEntries WHERE UserId=?", (account,))
        db.execute("DELETE FROM JournalNotes WHERE UserId=?", (account,))
        if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='JournalTopicOverviews'").fetchone():
            db.execute("DELETE FROM JournalTopicOverviews WHERE UserId=?", (account,))
        for entry in plan["entries"]:
            entry_id = str(uuid.uuid4()).upper()
            db.execute("INSERT INTO JournalEntries (Id,UserId,Text,SearchText,CreatedUtc,ReflectionJson,ForSession,IsDateOnly) VALUES (?,?,?,?,?,NULL,0,1)",
                       (entry_id, account, entry["text"], normalized(entry["text"]), entry["createdUtc"]))
            mapping = {"source": entry["source"], "entryId": entry_id, "date": entry["date"],
                       "textSha256": hashlib.sha256(entry["text"].encode()).hexdigest(), "files": []}
            for position, source in enumerate(entry["media"]):
                identifier = str(uuid.uuid4()).upper()
                destination = confined(account_dir, account_dir / (identifier.lower() + ".bin"))
                digest = sha256(source)
                with source.open("rb") as original, destination.open("xb") as target:
                    copied.append(destination)
                    shutil.copyfileobj(original, target)
                    target.flush()
                    os.fsync(target.fileno())
                if sha256(destination) != digest:
                    raise IOError("La copia de un adjunto no coincide con su original.")
                mime = MEDIA_TYPES.get(source.suffix.lower(), "application/octet-stream")
                status = "Disponible para analizar localmente" if mime in ("image/png", "image/jpeg", "image/webp") else "Importado de Diario · original conservado"
                db.execute("INSERT INTO JournalAttachments (Id,EntryId,Position,FileName,ContentType,Size,ExtractionStatus,ExtractedText,SearchText) VALUES (?,?,?,?,?,?,?,NULL,?)",
                           (identifier, entry_id, position, source.name, mime, source.stat().st_size, status, normalized(source.name)))
                mapping["files"].append({"attachmentId": identifier, "source": source.relative_to(plan["root"]).as_posix(), "sha256": digest})
            manifest["entries"].append(mapping)
        if db.execute("PRAGMA foreign_key_check").fetchall():
            raise ValueError("Falló la integridad referencial de la importación.")
        (backup_dir / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        db.execute("COMMIT")
        committed = True
        for identifier in old_ids:
            confined(account_dir, account_dir / (str(uuid.UUID(identifier)) + ".bin")).unlink(missing_ok=True)
            confined(account_dir, account_dir / (str(uuid.UUID(identifier)) + ".jpg")).unlink(missing_ok=True)
        return {**summary(plan), "replacedEntries": old_entries, "replacedNotes": old_notes,
                "backup": str(backup_dir), "verified": True}
    finally:
        if not committed:
            if db.in_transaction:
                db.execute("ROLLBACK")
            for path in copied:
                path.unlink(missing_ok=True)
        db.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--replace", action="store_true", help="Reemplazar entradas y notitas de la cuenta, con la aplicación detenida")
    parser.add_argument("--database", type=Path)
    parser.add_argument("--uploads", type=Path)
    parser.add_argument("--account")
    parser.add_argument("--backups", type=Path)
    args = parser.parse_args()
    plan = inspect_export(args.source)
    if args.replace:
        if not all((args.database, args.uploads, args.account, args.backups)):
            parser.error("--replace requiere --database, --uploads, --account y --backups")
        result = replace_journal(plan, args.database, args.uploads, args.account, args.backups)
    else:
        result = summary(plan)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
