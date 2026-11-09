from pathlib import Path
from contextlib import closing
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
import uuid

from import_journal import EntryParser, apple_filename, clean_text, inspect_export, replace_journal, summary


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mibitacora-apple-test-")
        self.root = Path(self.temp.name)
        self.source = self.root / "export"
        (self.source / "Entries").mkdir(parents=True)
        (self.source / "Resources").mkdir()
        self.account, self.other = str(uuid.uuid4()), str(uuid.uuid4())
        self.database, self.uploads = self.root / "test.db", self.root / "uploads"
        self.asset = str(uuid.uuid4()).upper()
        self.companion = str(uuid.uuid4()).upper()
        (self.source / "Resources" / (self.asset + ".png")).write_bytes(b"synthetic image bytes")
        (self.source / "Resources" / (self.asset + ".json")).write_text('{"associations":["' + self.companion + '"]}', encoding="utf-8")
        (self.source / "Resources" / (self.companion + ".mov")).write_bytes(b"synthetic video bytes")
        correct_name = "2024-01-02_Café.html"
        broken_name = correct_name.encode("utf-8").decode("cp437")
        (self.source / "Entries" / broken_name).write_text(self.html(), encoding="utf-8")
        (self.source / "index.html").write_text(f'<a href="Entries/{correct_name}">Entry</a>', encoding="utf-8")
        with closing(sqlite3.connect(self.database)) as db, db:
            db.executescript("""
                PRAGMA foreign_keys=ON;
                CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY);
                CREATE TABLE JournalEntries(Id TEXT PRIMARY KEY,UserId TEXT REFERENCES AspNetUsers(Id),Text TEXT,SearchText TEXT,
                    CreatedUtc TEXT,ReflectionJson TEXT,ForSession INTEGER,IsDateOnly INTEGER);
                CREATE UNIQUE INDEX EntryDate ON JournalEntries(UserId,CreatedUtc);
                CREATE TABLE JournalNotes(Id TEXT PRIMARY KEY,UserId TEXT REFERENCES AspNetUsers(Id),Text TEXT);
                CREATE TABLE JournalAttachments(Id TEXT PRIMARY KEY,EntryId TEXT REFERENCES JournalEntries(Id) ON DELETE CASCADE,
                    Position INTEGER,FileName TEXT,ContentType TEXT,Size INTEGER,ExtractionStatus TEXT,ExtractedText TEXT,SearchText TEXT);
                """)
            db.executemany("INSERT INTO AspNetUsers VALUES (?)", [(self.account,), (self.other,)])
            db.executemany("INSERT INTO JournalEntries VALUES (?,?,?,'','2024-01-01 00:00:00',NULL,0,0)",
                           [("old", self.account, "prueba"), ("other", self.other, "otro usuario")])
            db.execute("INSERT INTO JournalNotes VALUES ('oldnote',?,'prueba')", (self.account,))
            db.execute("INSERT INTO JournalNotes VALUES ('othernote',?,'conservar')", (self.other,))

    def tearDown(self):
        # TemporaryDirectory owns and confines its cleanup to the directory it created.
        self.temp.cleanup()

    def html(self):
        return f'''<html><head><style>.x {{ color:red; }}</style></head><body><p><span><div class="pageContainer">
            <div class="pageHeader">martes, 2 de enero de 2024</div>
            <div class="assetGrid"><div id="{self.asset}"><img src="../Resources/{self.asset}.png">Lugar de ejemplo</div></div>
            <div class='title'>Café &amp; apuntes</div><div class='bodyText'></span></p>
            <p>Primera <b>idea</b> &lt;literal&gt;.</p><p>Segunda línea.<br>Con emoji ☕.</p>
            <p><a href="https://example.com/">Referencia</a></p><p><span></div></div></span></p></body></html>'''

    def test_cocoa_markup_and_companions(self):
        plan = inspect_export(self.source)
        entry = plan["entries"][0]
        self.assertEqual(summary(plan)["attachments"], 2)
        self.assertEqual(entry["body"], "Primera idea <literal>.\n\nSegunda línea.\nCon emoji ☕.\n\nReferencia (https://example.com/)")
        self.assertTrue(entry["text"].startswith("Café & apuntes\n\nPrimera"))
        self.assertTrue(entry["text"].endswith("Lugar de ejemplo"))
        self.assertEqual(entry["createdUtc"], "2024-01-02 15:00:00.000000")
        self.assertEqual([file.suffix for file in entry["media"]], [".png", ".mov"])
        self.assertEqual(apple_filename("Cafe\u0301.html".encode().decode("cp437")), "Café.html")

    def test_replace_preserves_other_account_and_backups(self):
        result = replace_journal(inspect_export(self.source), self.database, self.uploads, self.account, self.root / "backups")
        self.assertTrue(result["verified"])
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM JournalEntries WHERE UserId=?", (self.account,)).fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM JournalNotes WHERE UserId=?", (self.account,)).fetchone()[0], 0)
            self.assertEqual(db.execute("SELECT Text FROM JournalEntries WHERE UserId=?", (self.other,)).fetchone()[0], "otro usuario")
            self.assertEqual(db.execute("SELECT COUNT(*) FROM JournalNotes WHERE UserId=?", (self.other,)).fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT IsDateOnly FROM JournalEntries WHERE UserId=?", (self.account,)).fetchone()[0], 1)
            for identifier, name in db.execute("SELECT Id,FileName FROM JournalAttachments"):
                self.assertEqual((self.uploads / self.account / (identifier.lower() + ".bin")).read_bytes(),
                                 (self.source / "Resources" / name).read_bytes())
        backup = Path(result["backup"])
        with closing(sqlite3.connect(backup / "mi-bitacora.db")) as db:
            self.assertEqual(db.execute("SELECT Text FROM JournalEntries WHERE Id='old'").fetchone()[0], "prueba")
        self.assertTrue((backup / "apple-export" / "index.html").is_file())
        self.assertTrue((backup / "manifest.json").is_file())

    def test_failed_copy_rolls_back_database(self):
        plan = inspect_export(self.source)
        with patch("import_journal.os.fsync", side_effect=IOError("disk full")):
            with self.assertRaises(IOError):
                replace_journal(plan, self.database, self.uploads, self.account, self.root / "backups")
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT Text FROM JournalEntries WHERE Id='old'").fetchone()[0], "prueba")
        self.assertEqual(list((self.uploads / self.account).glob("*.bin")), [])

    def test_replacement_removes_old_original_and_jpeg(self):
        old_file = str(uuid.uuid4())
        directory = self.uploads / self.account
        directory.mkdir(parents=True)
        (directory / (old_file + ".bin")).write_bytes(b"old original")
        (directory / (old_file + ".jpg")).write_bytes(b"old jpeg")
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute("INSERT INTO JournalAttachments VALUES (?,'old',0,'old.heic','image/heic',12,'',NULL,'')", (old_file.upper(),))
        result = replace_journal(inspect_export(self.source), self.database, self.uploads, self.account, self.root / "backups")
        self.assertFalse((directory / (old_file + ".bin")).exists())
        self.assertFalse((directory / (old_file + ".jpg")).exists())
        backup = Path(result["backup"]) / "uploads" / self.account
        self.assertEqual((backup / (old_file + ".bin")).read_bytes(), b"old original")
        self.assertEqual((backup / (old_file + ".jpg")).read_bytes(), b"old jpeg")

    def test_unassigned_file_blocks_replacement(self):
        (self.source / "Resources" / "unassigned.png").write_bytes(b"unassigned")
        plan = inspect_export(self.source)
        with self.assertRaisesRegex(ValueError, "sin una entrada"):
            replace_journal(plan, self.database, self.uploads, self.account, self.root / "backups")
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM JournalEntries WHERE Id='old'").fetchone()[0], 1)

    def test_replacement_removes_only_own_topic_overview(self):
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute("CREATE TABLE JournalTopicOverviews (UserId TEXT PRIMARY KEY, SnapshotJson TEXT)")
            db.executemany("INSERT INTO JournalTopicOverviews VALUES (?, '{}')", [(self.account,), (self.other,)])
        replace_journal(inspect_export(self.source), self.database, self.uploads, self.account, self.root / "backups")
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT UserId FROM JournalTopicOverviews").fetchall(), [(self.other,)])

    def test_paths_and_duplicate_index(self):
        (self.source / "index.html").write_text('<a href="Entries/../../outside.html">x</a>', encoding="utf-8")
        with self.assertRaises(ValueError):
            inspect_export(self.source)


if __name__ == "__main__":
    unittest.main()
