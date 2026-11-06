using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiBitacora.Services;

public sealed partial class GeminiJournalService
{
    // Base64 expands the input by about a third. Leave space below Gemini's 20 MB inline request limit.
    private const int InlineByteBudget = 12 * 1024 * 1024;
    private const int InlineFileLimit = 3;

    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif", "application/pdf"
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

    private static GeminiEvidence BuildEvidence(JournalContext context)
    {
        var sources = new Dictionary<string, EvidenceSource>(StringComparer.Ordinal);
        var promptSources = new List<PromptSource>();
        var inlineParts = new List<object>();
        var currentText = Excerpt(context.Current.Text, 12_000);

        AddSource("actual", "entrada actual", "Tu entrada actual", currentText);

        var searchTerms = Terms(currentText);
        foreach (var file in context.Current.Attachments)
        {
            searchTerms.UnionWith(Terms(file.FileName));
            searchTerms.UnionWith(Terms(Excerpt(file.ExtractedText, 800)));
        }

        var entries = context.History
            .Where(entry => entry.Id != context.Current.Id)
            .OrderByDescending(entry => entry.CreatedUtc)
            .ToArray();
        foreach (var entry in Prioritize(entries, searchTerms, entry => entry.Text, keepRecent: 3, limit: 9))
        {
            var label = DateLabel("Entrada", entry.CreatedUtc);
            AddSource($"entrada{promptSources.Count}", "entrada previa", label, Excerpt(entry.Text, 1150),
                priorResponse: Excerpt(entry.Reflection?.Overview, 450));
        }

        var notes = context.Notes.OrderByDescending(note => note.CreatedUtc).ToArray();
        foreach (var note in Prioritize(notes, searchTerms, note => note.Text, keepRecent: 2, limit: 6))
        {
            var label = DateLabel("Notita", note.CreatedUtc);
            AddSource($"notita{promptSources.Count}", "notita", label, Excerpt(note.Text, 1100));
        }

        var currentAttachments = context.Current.Attachments;
        var currentIds = currentAttachments.Select(file => file.Id).ToHashSet();
        var previousAttachments = context.Attachments
            .Where(file => !currentIds.Contains(file.Id) && file.EntryId != context.Current.Id)
            .DistinctBy(file => file.Id)
            .ToArray();
        var selectedAttachments = currentAttachments.Concat(Prioritize(previousAttachments, searchTerms,
            file => $"{file.FileName} {Excerpt(file.ExtractedText, 1800)}", keepRecent: 2, limit: 6));

        var binaryBytes = 0;
        var binaryFiles = 0;
        var unreadCurrentAttachments = 0;
        var media = context.Media.DistinctBy(file => file.AttachmentId).ToDictionary(file => file.AttachmentId);
        foreach (var file in selectedAttachments)
        {
            var isCurrent = currentIds.Contains(file.Id);
            var id = $"archivo{promptSources.Count}";
            var text = Excerpt(file.ExtractedText, isCurrent ? 3900 : 1500);
            var hasInlineContent = false;
            if (text.Length == 0 &&
                binaryFiles < InlineFileLimit &&
                media.TryGetValue(file.Id, out var binary) &&
                binary.Content.Length > 0 &&
                binary.Content.Length <= InlineByteBudget - binaryBytes &&
                InlineTypes.Contains(binary.ContentType) &&
                string.Equals(file.ContentType, binary.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                inlineParts.Add(new { text = $"Contenido adjunto de la fuente {id}." });
                inlineParts.Add(new
                {
                    inlineData = new
                    {
                        mimeType = binary.ContentType,
                        data = Convert.ToBase64String(binary.Content)
                    }
                });
                binaryBytes += binary.Content.Length;
                binaryFiles++;
                hasInlineContent = true;
            }

            if (isCurrent && !hasInlineContent && text.Length == 0)
            {
                unreadCurrentAttachments++;
            }

            var label = $"Archivo «{Excerpt(file.FileName, 150)}»";
            AddSource(id, isCurrent ? "adjunto actual" : "adjunto previo", label, text, hasInlineContent);
        }

        var profile = Excerpt(context.Profile.PersonalContext, 1800);
        if (profile.Length > 0)
        {
            AddSource("perfil", "contexto escrito en el perfil", "Tu contexto", profile);
        }

        var prompt = JsonSerializer.Serialize(new { fuentes = promptSources }, JsonOptions);
        var parts = new List<object>(1 + inlineParts.Count)
        {
            new { text = $"Ordená la entrada actual con las fuentes disponibles. Estos son los datos.\n{prompt}" }
        };
        parts.AddRange(inlineParts);
        return new(parts, sources, unreadCurrentAttachments);

        void AddSource(
            string id,
            string type,
            string label,
            string text,
            bool inline = false,
            string? priorResponse = null)
        {
            sources.Add(id, new(label, text, text.Length > 0 || inline));
            promptSources.Add(new(id, type, label, text, inline, priorResponse));
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
        string? DevolucionAnterior);

    private sealed record EvidenceSource(string Label, string Text, bool HasContent);

    private sealed record GeminiEvidence(
        IReadOnlyList<object> Parts,
        IReadOnlyDictionary<string, EvidenceSource> Sources,
        int UnreadCurrentAttachments);
}
