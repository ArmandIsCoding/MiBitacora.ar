using System.Text;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace MiBitacora.Services;

// Runs before web-host/configuration startup, in a bounded, cancellable child process.
public static class LocalPdfWorker
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return 2;
            if (args.Length != 5 || !int.TryParse(args[3], out var textPages) || !int.TryParse(args[4], out var visualPages)
                || textPages is < 1 or > 200 || visualPages is < 1 or > 4) return 2;
            var input = Path.GetFullPath(args[1]);
            var directory = Path.GetFullPath(args[2]);
            if (Path.GetDirectoryName(input) != directory || !Path.GetFileName(directory).StartsWith("local-", StringComparison.Ordinal)
                || new FileInfo(input).Length is <= 0 or > JournalStore.MaxFileBytes) return 2;
            using var pdf = PdfDocument.Open(input);
            if (pdf.NumberOfPages is < 1 or > 2000) return 3;
            var text = new StringBuilder();
            var pagesRead = 0;
            for (var page = 1; page <= Math.Min(textPages, pdf.NumberOfPages) && text.Length < 16000; page++)
            {
                text.AppendLine($"[Página {page} de {pdf.NumberOfPages}]");
                var start = text.Length;
                foreach (var word in pdf.GetPage(page).GetWords())
                {
                    var available = 16000 - text.Length;
                    if (available <= 0) break;
                    var take = Math.Min(word.Text.Length, available);
                    if (take > 0 && char.IsHighSurrogate(word.Text[take - 1])) take--;
                    text.Append(word.Text.AsSpan(0, take));
                    if (text.Length < 16000) text.Append(' ');
                }
                // A scanned page's number alone is not extracted document content.
                if (text.Length == start) text.Length -= $"[Página {page} de {pdf.NumberOfPages}]".Length + Environment.NewLine.Length;
                else if (text.Length < 15998) text.AppendLine();
                pagesRead = page;
            }
            var bytes = await File.ReadAllBytesAsync(input);
            var selected = Enumerable.Range(0, Math.Min(visualPages, pdf.NumberOfPages))
                .Select(i => visualPages == 1 || pdf.NumberOfPages == 1 ? 0
                    : (int)Math.Round(i * (pdf.NumberOfPages - 1d) / (Math.Min(visualPages, pdf.NumberOfPages) - 1)))
                .Distinct().ToArray();
            var images = new List<PdfWorkerImage>();
            foreach (var index in selected)
            {
                var page = pdf.GetPage(index + 1);
                var width = page.Width; var height = page.Height;
                if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || width > 14400 || height > 14400) return 3;
                var scale = Math.Min(1600 / width, 1600 / height);
                using var bitmap = Conversion.ToImage(bytes, page: index, options: new RenderOptions(
                    Width: Math.Max(1, (int)Math.Round(width * scale)), Height: Math.Max(1, (int)Math.Round(height * scale)), WithFormFill: false));
                using var image = SKImage.FromBitmap(bitmap);
                using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                var name = $"page-{index + 1}.jpg";
                await File.WriteAllBytesAsync(Path.Combine(directory, name), jpeg.ToArray());
                images.Add(new(name, $"Página {index + 1} de {pdf.NumberOfPages}"));
            }
            var coverage = $"PDF · texto de hasta {pagesRead} de {pdf.NumberOfPages} páginas, máximo 16.000 caracteres; muestra visual: páginas {string.Join(", ", selected.Select(i => i + 1))}. Las demás páginas no se examinaron visualmente.";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new PdfWorkerResult(text.ToString().Trim(), coverage, images)));
            return 0;
        }
        catch { Console.Error.WriteLine("PDF inválido, protegido o no compatible con la lectura local."); return 1; }
    }
}

internal sealed record PdfWorkerImage(string File, string SourcePart);
internal sealed record PdfWorkerResult(string Text, string Coverage, IReadOnlyList<PdfWorkerImage> Images);
