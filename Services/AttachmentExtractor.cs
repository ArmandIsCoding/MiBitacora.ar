using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ExcelDataReader;

namespace MiBitacora.Services;

internal static class AttachmentExtractor
{
    private const int TextLimit = 16_000;
    private const long ExpandedArchiveLimit = 32 * 1024 * 1024;
    private const long ExpandedPartLimit = 8 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".yml", ".yaml", ".log", ".ini"
    };

    static AttachmentExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string CleanFileName(string name)
    {
        name = name.Replace('\\', '/').Split('/')[^1].Trim();
        name = string.Concat(name.Where(character =>
            !char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "Adjunto";
        }

        if (name.Length <= 180)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        return extension.Length is > 0 and <= 16
            ? Clip(name, 180 - extension.Length) + extension
            : Clip(name, 180);
    }

    public static bool CanAnalyzeLocally(string type) =>
        type is "image/jpeg" or "image/png" or "image/webp" or "image/heic" or "image/heif";

    public static AttachmentAnalysis Analyze(string name, byte[] content, CancellationToken cancellationToken)
    {
        var mediaType = DetectMediaType(content);
        if (mediaType is not null)
        {
            return new AttachmentAnalysis(mediaType, null, CanAnalyzeLocally(mediaType)
                ? "Disponible para analizar localmente"
                : "Guardado · formato sin análisis visual");
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = extension switch
            {
                ".docx" or ".pptx" or ".odt" => ReadDocument(extension, content, cancellationToken),
                ".xls" or ".xlsx" or ".xlsm" or ".xlsb" => ReadWorkbook(extension, content, cancellationToken),
                _ when TextExtensions.Contains(extension) => ReadText(content),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Trim();
                var isPartial = text.Length > TextLimit;
                return new AttachmentAnalysis("application/octet-stream", Clip(text, TextLimit),
                    isPartial ? "Fragmento listo para usar" : "Texto listo para usar");
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return new AttachmentAnalysis("application/octet-stream", null, "Guardado · no se pudo leer el contenido");
        }

        return new AttachmentAnalysis("application/octet-stream", null, "Guardado · sin texto disponible");
    }

    private static string? DetectMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (bytes.Length >= 16 && bytes[4..8].SequenceEqual("ftyp"u8))
        {
            var end = Math.Min(Math.Min(BinaryPrimitives.ReadUInt32BigEndian(bytes), (uint)bytes.Length), 1024);
            var genericHeif = false;
            for (var index = 8; index + 4 <= end; index += index == 8 ? 8 : 4)
            {
                var brand = bytes.Slice(index, 4);
                if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return "image/avif";
                if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
                    || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)) return "image/heic";

                genericHeif |= brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
            }

            if (genericHeif) return "image/heif";
        }

        return null;
    }

    private static string? ReadText(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        string text;
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            text = ReadExcerpt(reader);
        }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.GetEncoding(1252));
            text = ReadExcerpt(reader);
        }

        return text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
            ? null
            : text;
    }

    private static string ReadExcerpt(TextReader reader)
    {
        var buffer = new char[TextLimit + 1];
        return new string(buffer, 0, reader.ReadBlock(buffer));
    }

    private static string ReadDocument(string extension, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        ValidateArchive(archive, cancellationToken);

        var output = new StringBuilder();
        switch (extension)
        {
            case ".docx":
                ReadParagraphs(RequiredPart(archive, "word/document.xml"),
                    "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "p", "t", output, cancellationToken);
                break;
            case ".odt":
                ReadParagraphs(RequiredPart(archive, "content.xml"),
                    "urn:oasis:names:tc:opendocument:xmlns:text:1.0", "p", null, output, cancellationToken);
                break;
            case ".pptx":
                var slides = archive.Entries.Where(entry => entry.FullName == $"ppt/slides/{entry.Name}" && entry.Name.StartsWith("slide", StringComparison.Ordinal)
                        && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)
                        && int.TryParse(Path.GetFileNameWithoutExtension(entry.Name).AsSpan(5), out _))
                    .OrderBy(entry => int.Parse(Path.GetFileNameWithoutExtension(entry.Name).AsSpan(5), CultureInfo.InvariantCulture));
                foreach (var slide in slides)
                {
                    ReadParagraphs(slide, "http://schemas.openxmlformats.org/drawingml/2006/main",
                        "p", "t", output, cancellationToken);
                    if (output.Length > TextLimit) break;
                }
                break;
        }

        return output.ToString();
    }

    private static ZipArchiveEntry RequiredPart(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException("Falta una sección del documento.");

    private static void ReadParagraphs(ZipArchiveEntry part, XNamespace xmlNamespace, string paragraphName, string? textName,
        StringBuilder output, CancellationToken cancellationToken)
    {
        using var stream = part.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ExpandedPartLimit
        });

        while (output.Length <= TextLimit && xml.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (xml.NodeType != XmlNodeType.Element || xml.NamespaceURI != xmlNamespace.NamespaceName || xml.LocalName != paragraphName)
            {
                continue;
            }

            using var subtree = xml.ReadSubtree();
            var paragraph = XElement.Load(subtree);
            var text = textName is null
                ? paragraph.Value
                : string.Concat(paragraph.Descendants(xmlNamespace + textName).Select(element => element.Value));
            if (!string.IsNullOrWhiteSpace(text))
            {
                Append(output, text);
                output.AppendLine();
            }
        }
    }

    private static string ReadWorkbook(string extension, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        if (extension != ".xls")
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            ValidateArchive(archive, cancellationToken);
            stream.Position = 0;
        }

        using var reader = extension == ".xls"
            ? ExcelReaderFactory.CreateBinaryReader(stream)
            : ExcelReaderFactory.CreateOpenXmlReader(stream);

        var output = new StringBuilder();
        var hasContent = false;
        const int sheetLimit = 12;
        const int rowLimit = 160;
        const int columnLimit = 40;
        var sheetIndex = 0;

        do
        {
            Append(output, $"Hoja: {reader.Name}");
            output.AppendLine();
            var columnCount = Math.Min(reader.FieldCount, columnLimit);
            var rowIndex = 0;
            while (rowIndex < rowLimit && output.Length <= TextLimit && reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowIndex++;
                var values = new string[columnCount];
                for (var column = 0; column < columnCount; column++)
                {
                    values[column] = Clip(FormatCell(reader.GetValue(column)), 400);
                }

                if (values.All(string.IsNullOrWhiteSpace)) continue;
                hasContent = true;
                Append(output, string.Join(" | ", values).TrimEnd(' ', '|'));
                output.AppendLine();
            }

            if (reader.FieldCount > columnLimit || reader.RowCount > rowLimit)
            {
                Append(output, $"[Vista parcial: hasta {rowLimit} filas y {columnLimit} columnas.]");
                output.AppendLine();
            }
        } while (++sheetIndex < sheetLimit && output.Length <= TextLimit && reader.NextResult());

        if (reader.ResultsCount > sheetLimit)
        {
            Append(output, $"[Vista parcial: hasta {sheetLimit} hojas.]");
        }

        return hasContent ? output.ToString() : "";
    }

    private static string FormatCell(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static void ValidateArchive(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > 512 || archive.Entries.Sum(entry => entry.Length) > ExpandedArchiveLimit)
        {
            throw new InvalidDataException("El documento supera el límite de lectura.");
        }

        var buffer = new byte[16 * 1024];
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > ExpandedPartLimit)
            {
                throw new InvalidDataException("Una sección del documento supera el límite de lectura.");
            }

            using var stream = entry.Open();
            long total = 0;
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += count;
                if (total > entry.Length)
                {
                    throw new InvalidDataException("El tamaño de una sección del documento no coincide.");
                }
            }

            if (total != entry.Length)
            {
                throw new InvalidDataException("Una sección del documento está incompleta.");
            }
        }
    }

    private static void Append(StringBuilder output, string text) =>
        output.Append(Clip(text, Math.Max(0, TextLimit + 1 - output.Length)));

    private static string Clip(string text, int limit)
    {
        if (text.Length <= limit) return text;
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }
}

internal sealed record AttachmentAnalysis(string ContentType, string? ExtractedText, string Status);
