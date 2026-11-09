using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiBitacora.Services;

public abstract partial class JournalAiService
{
    // Limit image payload and selected text for the pilot's 8192-token context.
    private const int InlineByteBudget = 12 * 1024 * 1024;
    private const int InlineFileLimit = 2;

    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "algo", "algunas", "algunos", "antes", "cada", "como", "conmigo", "cual", "cuando", "desde",
        "despues", "donde", "ella", "ellas", "ellos", "entre", "esta", "estas", "este", "esto", "estos",
        "estoy", "hace", "hacia", "hasta", "mucho", "muchas", "muchos", "nada", "nadie", "otra", "otras",
        "otro", "otros", "para", "pero", "porque", "puede", "puedo", "queria", "quiero", "sobre", "solo",
        "tambien", "tampoco", "tener", "tengo", "toda", "todas", "todo", "todos", "unas", "unos", "varias"
    };

    private static readonly Regex TermPattern = new(
        @"[\p{L}\p{N}]{4,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static JournalEvidence BuildEvidence(JournalContext context)
    {
        var sources = new Dictionary<string, EvidenceSource>(StringComparer.Ordinal);
        var promptSources = new List<PromptSource>();
        var images = new List<JournalImage>();
        var remainingText = 6500;
        var currentText = Excerpt(context.Current.Text, 3000);

        AddSource("actual", "entrada actual", "Tu entrada actual", currentText);

        var searchTerms = Terms(currentText);
        foreach (var file in context.Current.Attachments)
        {
            searchTerms.UnionWith(Terms(file.FileName));
            searchTerms.UnionWith(Terms(Excerpt(file.ExtractedText, 800)));
        }

        var currentAttachments = context.Current.Attachments;
        var currentIds = currentAttachments.Select(file => file.Id).ToHashSet();
        var previousAttachments = context.Attachments
            .Where(file => !currentIds.Contains(file.Id) && file.EntryId != context.Current.Id)
            .DistinctBy(file => file.Id)
            .ToArray();
        var selectedAttachments = currentAttachments.Concat(Prioritize(previousAttachments, searchTerms,
            file => $"{file.FileName} {Excerpt(file.ExtractedText, 1800)}", keepRecent: 1, limit: 2));

        var binaryBytes = 0;
        var binaryFiles = 0;
        var unreadCurrentAttachments = 0;
        var media = context.Media.DistinctBy(file => file.AttachmentId).ToDictionary(file => file.AttachmentId);
        foreach (var file in selectedAttachments)
        {
            var isCurrent = currentIds.Contains(file.Id);
            var id = $"archivo{promptSources.Count}";
            var text = Excerpt(file.ExtractedText, isCurrent ? 1000 : 400);
            var hasInlineContent = false;
            if (text.Length == 0 &&
                binaryFiles < InlineFileLimit &&
                media.TryGetValue(file.Id, out var binary) &&
                binary.Content.Length > 0 &&
                binary.Content.Length <= InlineByteBudget - binaryBytes &&
                InlineTypes.Contains(binary.ContentType) &&
                string.Equals(HeicImageService.AnalysisContentType(file.ContentType), binary.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                images.Add(new(Convert.ToBase64String(binary.Content), binary.ContentType));
                binaryBytes += binary.Content.Length;
                binaryFiles++;
                hasInlineContent = true;
            }

            var label = $"Archivo «{Excerpt(file.FileName, 150)}»";
            AddSource(id, isCurrent ? "adjunto actual" : "adjunto previo", label, text, hasInlineContent, imageNumber: hasInlineContent ? binaryFiles : null);
            if (isCurrent && !sources[id].HasContent) unreadCurrentAttachments++;
        }

        var entries = context.History
            .Where(entry => entry.Id != context.Current.Id)
            .OrderByDescending(entry => entry.CreatedUtc)
            .ToArray();
        foreach (var entry in Prioritize(entries, searchTerms, entry => entry.Text, keepRecent: 1, limit: 3))
        {
            var label = DateLabel("Entrada", entry.CreatedUtc);
            AddSource($"entrada{promptSources.Count}", "entrada previa", label, Excerpt(entry.Text, 500),
                priorResponse: Excerpt(entry.Reflection?.Overview, 150));
        }

        var notes = context.Notes.OrderByDescending(note => note.CreatedUtc).ToArray();
        foreach (var note in Prioritize(notes, searchTerms, note => note.Text, keepRecent: 1, limit: 2))
        {
            var label = DateLabel("Notita", note.CreatedUtc);
            AddSource($"notita{promptSources.Count}", "notita", label, Excerpt(note.Text, 400));
        }

        var profile = Excerpt(context.Profile.PersonalContext, 600);
        if (profile.Length > 0)
        {
            AddSource("perfil", "contexto escrito en el perfil", "Tu contexto", profile);
        }

        var prompt = JsonSerializer.Serialize(new { fuentes = promptSources }, JsonOptions);
        var promptText = "Ordená la entrada actual con estas fuentes. Se incluyen fragmentos, no el cuaderno completo. " +
            "Las imágenes adjuntas corresponden en orden al campo numeroImagen (1, 2). " +
            "Si un texto está cortado, no inventes la continuación.\n" + prompt;
        return new(promptText, images, sources, unreadCurrentAttachments);

        void AddSource(
            string id,
            string type,
            string label,
            string text,
            bool inline = false,
            string? priorResponse = null,
            int? imageNumber = null)
        {
            text = Excerpt(text, Math.Min(remainingText, text.Length));
            remainingText -= text.Length;
            priorResponse = Excerpt(priorResponse, Math.Min(remainingText, 150));
            remainingText -= priorResponse.Length;
            sources.Add(id, new(label, text, text.Length > 0 || inline));
            promptSources.Add(new(id, type, label, text, inline, priorResponse, imageNumber));
        }
    }

    private static IEnumerable<T> Prioritize<T>(
        IReadOnlyList<T> newestFirst,
        HashSet<string> searchTerms,
        Func<T, string> text,
        int keepRecent,
        int limit)
    {
        var recent = newestFirst.Take(keepRecent);
        var relevant = newestFirst.Skip(keepRecent)
            .Select(item => new { Item = item, MatchCount = Terms(text(item)).Count(searchTerms.Contains) })
            .OrderByDescending(match => match.MatchCount)
            .Take(limit - keepRecent)
            .Select(match => match.Item);

        return recent.Concat(relevant);
    }

    private static HashSet<string> Terms(string? value)
    {
        var text = Excerpt(value, 12_000).Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var normalized = string.Concat(text.Where(character =>
            CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark));

        return TermPattern.Matches(normalized)
            .Select(match => match.Value)
            .Where(term => !CommonWords.Contains(term))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Excerpt(string? value, int length)
    {
        if (length <= 0) return string.Empty;
        var text = value?.Trim() ?? string.Empty;
        if (text.Length <= length)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..end].TrimEnd();
    }

    private sealed record PromptSource(
        string Id,
        string Tipo,
        string Etiqueta,
        string Texto,
        bool IncluyeArchivo,
        string? DevolucionAnterior,
        int? NumeroImagen);

    private sealed record EvidenceSource(string Label, string Text, bool HasContent);

    private sealed record JournalEvidence(
        string Prompt,
        IReadOnlyList<JournalImage> Images,
        IReadOnlyDictionary<string, EvidenceSource> Sources,
        int UnreadCurrentAttachments);
}
