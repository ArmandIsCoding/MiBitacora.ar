using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed partial class JournalStore(IDbContextFactory<ApplicationDbContext> factory, UploadFileStore uploads,
    IOptions<JournalStorageOptions>? storageOptions = null, HeicImageService? imageService = null) : IJournalStore
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxEntryAttachmentBytes = 16 * 1024 * 1024;
    public const int MaxTextLength = 12_000;
    public const int MaxNoteLength = 2_000;
    public const int MaxDisplayNameLength = 80;
    public const int MaxPersonalContextLength = 3_000;

    private readonly JournalStorageOptions storage = storageOptions?.Value ?? new();
    private readonly HeicImageService images = imageService ?? new(uploads);
    private const int MaxNotes = 500;
    private const int ContextAttachmentChars = 24_000;
    private const int ContextMediaBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> CommonWords =
    [
        "ahora", "algo", "antes", "como", "cuando", "desde", "donde", "ella", "ellos",
        "esta", "estaba", "estan", "este", "esto", "estoy", "habia", "hacia", "hago", "hasta",
        "luego", "mucho", "nada", "otra", "otro", "para", "pero", "porque", "puede", "quiero",
        "sobre", "tambien", "tengo", "tiene", "toda", "todo", "tras", "unas", "unos"
    ];

    public async Task<JournalProfile> GetProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadProfileAsync(db, userId, cancellationToken);
    }

    public async Task SaveProfileAsync(string userId, JournalProfile profile, CancellationToken cancellationToken = default)
    {
        var displayName = profile.DisplayName.Trim();
        var context = profile.PersonalContext.Trim();
        RequireLength(displayName, MaxDisplayNameLength, "Tu nombre");
        RequireLength(context, MaxPersonalContextLength, "Tu contexto");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.JournalProfiles.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (row is null)
        {
            row = new JournalProfileRow { UserId = userId };
            db.JournalProfiles.Add(row);
        }

        row.DisplayName = displayName;
        row.PersonalContext = context;
        row.GeminiConsent = profile.GeminiConsent;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JournalEntry>> GetEntriesAsync(string userId, int take = 40, DateTime? beforeUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = db.JournalEntries.Where(row => row.UserId == userId);
        if (beforeUtc is { } before)
        {
            entries = entries.Where(row => row.CreatedUtc < before);
        }

        var limit = take == int.MaxValue ? take : Math.Clamp(take, 1, 100);
        var page = await ReadEntriesAsync(entries.OrderByDescending(row => row.CreatedUtc).Take(limit), cancellationToken);
        page.Reverse();
        return page;
    }

    public async Task<IReadOnlyDictionary<DateOnly, int>> GetEntryDaysAsync(string userId, TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var timestamps = db.JournalEntries.Where(row => row.UserId == userId).Select(row => row.CreatedUtc);
        var counts = new Dictionary<DateOnly, int>();

        await foreach (var createdUtc in timestamps.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(createdUtc), timeZone));
            counts[day] = counts.GetValueOrDefault(day) + 1;
        }

        return counts;
    }

    public async Task<IReadOnlyList<JournalEntry>> GetEntriesInRangeAsync(string userId, DateTime fromUtc, DateTime untilUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadEntriesAsync(db.JournalEntries
            .Where(row => row.UserId == userId && row.CreatedUtc >= fromUtc && row.CreatedUtc < untilUtc)
            .OrderBy(row => row.CreatedUtc), cancellationToken);
    }

    public async Task<JournalEntry> AddEntryAsync(string userId, string text, IReadOnlyList<PendingAttachment> files,
        CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        RequireLength(text, MaxTextLength, "Tu entrada");
        if (text.Length == 0 && files.Count == 0)
        {
            throw new JournalInputException("Escribí algo o agregá un adjunto para guardar la entrada.");
        }

        ValidateFiles(files);
        using var uploadLock = await uploads.LockAsync(cancellationToken);

        var entry = new JournalEntryRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Text = text,
            SearchText = NormalizeSearch(text),
            Attachments = PrepareAttachments(files, cancellationToken)
        };

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (entry.Attachments.Count > 0)
        {
            var quota = await db.JournalAttachments
                .Where(row => row.Entry.UserId == userId)
                .GroupBy(row => row.Entry.UserId)
                .Select(group => new { Bytes = group.Sum(row => row.Size), Count = group.Count() })
                .SingleOrDefaultAsync(cancellationToken);
            if ((quota?.Bytes ?? 0) + entry.Attachments.Sum(row => row.Size) > storage.MaxAccountAttachmentBytes
                || (quota?.Count ?? 0) + entry.Attachments.Count > storage.MaxAccountFiles)
            {
                throw new JournalInputException("Alcanzaste el espacio de adjuntos del piloto. Podés guardar la entrada sin archivos o liberar espacio.");
            }
        }

        // A strictly increasing timestamp keeps cursor-based paging stable across tabs.
        var latest = await db.JournalEntries.Where(row => row.UserId == userId)
            .MaxAsync(row => (DateTime?)row.CreatedUtc, cancellationToken);
        var now = DateTime.UtcNow;
        entry.CreatedUtc = latest is { } previous && previous >= now ? previous.AddTicks(1) : now;

        db.JournalEntries.Add(entry);
        var written = new List<Guid>();
        try
        {
            // Validate foreign keys before writing any originals; commit only after all files exist.
            await db.SaveChangesAsync(cancellationToken);
            for (var index = 0; index < files.Count; index++)
            {
                var id = entry.Attachments[index].Id;
                await uploads.WriteAsync(userId, id, files[index].Content, cancellationToken);
                written.Add(id);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            uploads.Delete(userId, written);
            throw;
        }

        return new JournalEntry(entry.Id, entry.Text, entry.CreatedUtc, null, false,
            entry.Attachments.Select(ToAttachment).ToArray());
    }

    public async Task SaveReflectionAsync(string userId, Guid entryId, JournalReflection reflection,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.JournalEntries.SingleOrDefaultAsync(row => row.Id == entryId && row.UserId == userId,
            cancellationToken);
        if (entry is null)
        {
            throw EntryUnavailable();
        }

        entry.ReflectionJson = JsonSerializer.Serialize(reflection, JsonOptions);
        var themes = string.Join(" ", reflection.Themes.Select(theme => theme.Title));
        entry.SearchText = NormalizeSearch($"{entry.Text}\n{reflection.Title}\n{reflection.Overview}\n{themes}");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetEntryForSessionAsync(string userId, Guid entryId, bool selected,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var count = await db.JournalEntries.Where(row => row.Id == entryId && row.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ForSession, selected), cancellationToken);
        if (count == 0) throw EntryUnavailable();
    }

    public async Task DeleteEntryAsync(string userId, Guid entryId, CancellationToken cancellationToken = default)
    {
        using var uploadLock = await uploads.LockAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var files = await db.JournalAttachments.Where(row => row.EntryId == entryId && row.Entry.UserId == userId)
            .Select(row => row.Id).ToListAsync(cancellationToken);
        await db.JournalEntries.Where(row => row.Id == entryId && row.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        uploads.Delete(userId, files);
    }

    public async Task<IReadOnlyList<JournalNote>> GetNotesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadNotesAsync(db, userId, cancellationToken);
    }

    public async Task<JournalNote> SaveNoteAsync(string userId, Guid? noteId, string text,
        CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        RequireLength(text, MaxNoteLength, "Tu notita");
        if (text.Length == 0) throw new JournalInputException("Escribí algo para guardar la notita.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        JournalNoteRow row;
        if (noteId is { } existingId)
        {
            row = await db.JournalNotes.SingleOrDefaultAsync(item => item.Id == existingId && item.UserId == userId,
                cancellationToken) ?? throw NoteUnavailable();
        }
        else
        {
            var count = await db.JournalNotes.CountAsync(item => item.UserId == userId, cancellationToken);
            if (count >= MaxNotes)
            {
                throw new JournalInputException("Alcanzaste el límite de notitas del piloto.");
            }

            row = new JournalNoteRow { Id = Guid.NewGuid(), UserId = userId, CreatedUtc = DateTime.UtcNow };
            db.JournalNotes.Add(row);
        }

        row.Text = text;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToNote(row);
    }

    public async Task SetNoteForSessionAsync(string userId, Guid noteId, bool selected,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var count = await db.JournalNotes.Where(row => row.Id == noteId && row.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ForSession, selected), cancellationToken);
        if (count == 0) throw NoteUnavailable();
    }

    public async Task DeleteNoteAsync(string userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.JournalNotes.Where(row => row.Id == noteId && row.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JournalEntry>> GetSessionEntriesAsync(string userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadEntriesAsync(db.JournalEntries
            .Where(row => row.UserId == userId && row.ForSession)
            .OrderBy(row => row.CreatedUtc), cancellationToken);
    }

    public async Task<JournalContext> GetContextAsync(string userId, Guid entryId, bool includeMedia,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var owned = db.JournalEntries.Where(row => row.UserId == userId);
        var current = (await ReadEntriesAsync(owned.Where(row => row.Id == entryId), cancellationToken)).SingleOrDefault()
            ?? throw EntryUnavailable();
        var profile = await ReadProfileAsync(db, userId, cancellationToken);
        var terms = SearchTerms(current);
        var earlier = owned.Where(row => row.CreatedUtc < current.CreatedUtc);
        var history = await FindHistoryAsync(earlier, terms, cancellationToken);
        var notes = SelectNotes(await ReadNotesAsync(db, userId, cancellationToken), terms);

        var earlierFiles = db.JournalAttachments.Where(row => row.Entry.UserId == userId
            && row.Entry.CreatedUtc < current.CreatedUtc);
        var relatedFiles = terms.Length == 0 ? [] : await ProjectAttachments(
            earlierFiles.Where(row => terms.Any(term => row.SearchText.Contains(term)))
                .OrderByDescending(row => terms.Count(term => row.SearchText.Contains(term)))
                .ThenByDescending(row => row.Entry.CreatedUtc)
                .ThenBy(row => row.Position).Take(12)).ToListAsync(cancellationToken);
        var attachments = LimitAttachmentContext(current.Attachments
            .Concat(relatedFiles)
            .Concat(Enumerable.Reverse(history).SelectMany(entry => entry.Attachments)));

        var media = includeMedia
            ? await ReadMediaAsync(db, userId, attachments, cancellationToken)
            : [];

        var boundedHistory = history.Select(entry => entry with { Text = Clip(entry.Text, 2_500), Attachments = [] }).ToArray();
        return new JournalContext(profile, current, boundedHistory, notes, attachments, media);
    }

    public async Task<AttachmentContent?> GetAttachmentAsync(string userId, Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var metadata = await db.JournalAttachments.Where(row => row.Id == attachmentId && row.Entry.UserId == userId)
            .Select(row => new { row.FileName, row.ContentType })
            .SingleOrDefaultAsync(cancellationToken);
        if (metadata is null) return null;
        var stream = uploads.OpenRead(userId, attachmentId);
        return stream is null ? null : new AttachmentContent(metadata.FileName, metadata.ContentType, stream);
    }

    public async Task<AttachmentContent?> GetAttachmentPreviewAsync(string userId, Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var file = await db.JournalAttachments.Where(row => row.Id == attachmentId && row.Entry.UserId == userId)
            .Select(row => new { row.FileName, row.ContentType }).SingleOrDefaultAsync(cancellationToken);
        if (file is null || !HeicImageService.HasPreview(file.ContentType)) return null;
        if (!HeicImageService.IsHeic(file.ContentType))
        {
            var original = uploads.OpenRead(userId, attachmentId);
            return original is null ? null : new AttachmentContent(file.FileName, file.ContentType, original);
        }
        try
        {
            var jpeg = await images.OpenJpegAsync(userId, attachmentId, cancellationToken);
            return jpeg is null ? null : new AttachmentContent(Path.ChangeExtension(file.FileName, ".jpg"), "image/jpeg", jpeg);
        }
        catch (JournalInputException) { return null; }
    }

    private static async Task<List<JournalEntry>> FindHistoryAsync(IQueryable<JournalEntryRow> earlier, string[] terms,
        CancellationToken cancellationToken)
    {
        var recent = await ReadEntriesAsync(earlier.OrderByDescending(row => row.CreatedUtc).Take(8), cancellationToken);
        var matches = terms.Length == 0 ? [] : await ReadEntriesAsync(
            earlier.Where(row => terms.Any(term => row.SearchText.Contains(term)))
                .OrderByDescending(row => terms.Count(term => row.SearchText.Contains(term)))
                .ThenByDescending(row => row.CreatedUtc).Take(8), cancellationToken);
        return recent.Concat(matches).DistinctBy(entry => entry.Id).OrderBy(entry => entry.CreatedUtc).ToList();
    }

    private static JournalNote[] SelectNotes(IReadOnlyList<JournalNote> notes, string[] terms)
    {
        var related = notes.Select(note => new { Note = note, Score = MatchCount(note.Text, terms) })
            .Where(item => item.Score > 0).OrderByDescending(item => item.Score).Take(6).Select(item => item.Note);
        return notes.Take(8).Concat(related).DistinctBy(note => note.Id)
            .Select(note => note with { Text = Clip(note.Text, 1_200) }).ToArray();
    }

    private static JournalAttachment[] LimitAttachmentContext(IEnumerable<JournalAttachment> candidates)
    {
        var remaining = ContextAttachmentChars;
        return candidates.DistinctBy(attachment => attachment.Id).Take(20).Select(attachment =>
        {
            if (attachment.ExtractedText is not { } text) return attachment;
            var excerpt = Clip(text, Math.Min(remaining, 6_000));
            remaining -= excerpt.Length;
            return attachment with { ExtractedText = excerpt.Length > 0 ? excerpt : null };
        }).ToArray();
    }

    private async Task<JournalMediaFile[]> ReadMediaAsync(ApplicationDbContext db, string userId,
        IReadOnlyList<JournalAttachment> attachments, CancellationToken cancellationToken)
    {
        var remaining = ContextMediaBytes;
        var selected = attachments.Where(file => AttachmentExtractor.CanAnalyzeLocally(file.ContentType))
            .Select(file => file.Id).ToList();
        if (selected.Count == 0) return [];
        var metadata = await db.JournalAttachments
            .Where(row => selected.Contains(row.Id) && row.Entry.UserId == userId)
            .Select(row => new { row.Id, row.FileName, row.ContentType, row.Size })
            .ToListAsync(cancellationToken);
        var media = new List<JournalMediaFile>();
        foreach (var file in metadata.OrderBy(file => selected.IndexOf(file.Id)))
        {
            if (media.Count == 3) break;
            var heic = HeicImageService.IsHeic(file.ContentType);
            Stream? source;
            if (heic)
            {
                try { source = await images.OpenJpegAsync(userId, file.Id, cancellationToken); }
                catch (JournalInputException) { continue; } // Reported as an unread attachment by the reflector.
            }
            else source = uploads.OpenRead(userId, file.Id);
            await using var stream = source;
            if (stream is null || (!heic && stream.Length != file.Size))
                throw new JournalInputException("No pudimos leer una imagen adjunta. Revisá los archivos del servidor.");
            if (stream.Length > MaxFileBytes || stream.Length > remaining) continue;
            var content = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(content, cancellationToken);
            remaining -= content.Length;
            media.Add(new JournalMediaFile(file.Id, heic ? Path.ChangeExtension(file.FileName, ".jpg") : file.FileName,
                HeicImageService.AnalysisContentType(file.ContentType), content));
        }
        return media.ToArray();
    }

    private static async Task<JournalProfile> ReadProfileAsync(ApplicationDbContext db, string userId,
        CancellationToken cancellationToken) =>
        await db.JournalProfiles.Where(row => row.UserId == userId)
            .Select(row => new JournalProfile(row.DisplayName, row.PersonalContext, row.GeminiConsent))
            .SingleOrDefaultAsync(cancellationToken) ?? new JournalProfile("", "", false);

    private static async Task<List<JournalNote>> ReadNotesAsync(ApplicationDbContext db, string userId,
        CancellationToken cancellationToken)
    {
        var notes = await db.JournalNotes.AsNoTracking().Where(row => row.UserId == userId)
            .OrderByDescending(row => row.CreatedUtc).ThenBy(row => row.Id).ToListAsync(cancellationToken);
        return notes.Select(ToNote).ToList();
    }

    private static async Task<List<JournalEntry>> ReadEntriesAsync(IQueryable<JournalEntryRow> entries,
        CancellationToken cancellationToken)
    {
        var rows = await entries.AsSplitQuery().Select(row => new EntryProjection(
            row.Id, row.Text, row.CreatedUtc, row.ReflectionJson, row.ForSession, row.IsDateOnly, row.Attachments
                .OrderBy(attachment => attachment.Position)
                .Select(attachment => new JournalAttachment(attachment.Id, attachment.EntryId, attachment.FileName,
                    attachment.ContentType, attachment.Size, attachment.ExtractionStatus, attachment.ExtractedText))
                .ToList())).ToListAsync(cancellationToken);
        return rows.Select(row => new JournalEntry(row.Id, row.Text, AsUtc(row.CreatedUtc),
            ReadReflection(row.ReflectionJson), row.ForSession, row.Attachments, row.IsDateOnly)).ToList();
    }

    private static IQueryable<JournalAttachment> ProjectAttachments(IQueryable<JournalAttachmentRow> query) =>
        query.Select(row => new JournalAttachment(row.Id, row.EntryId, row.FileName, row.ContentType, row.Size,
            row.ExtractionStatus, row.ExtractedText));

    private static JournalReflection? ReadReflection(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<JournalReflection>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JournalAttachment ToAttachment(JournalAttachmentRow row) =>
        new(row.Id, row.EntryId, row.FileName, row.ContentType, row.Size, row.ExtractionStatus, row.ExtractedText);

    private static JournalNote ToNote(JournalNoteRow row) => new(row.Id, row.Text, AsUtc(row.CreatedUtc), row.ForSession);
    private static DateTime AsUtc(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc);

    private static void ValidateFiles(IReadOnlyList<PendingAttachment> files)
    {
        if (files.Any(file => file.Content.Length is 0 or > MaxFileBytes))
        {
            throw new JournalInputException("Cada archivo tiene que pesar entre 1 byte y 8 MB.");
        }

        if (files.Sum(file => (long)file.Content.Length) > MaxEntryAttachmentBytes)
        {
            throw new JournalInputException("Los adjuntos juntos pueden pesar hasta 16 MB por entrada.");
        }
    }

    private static List<JournalAttachmentRow> PrepareAttachments(IReadOnlyList<PendingAttachment> files,
        CancellationToken cancellationToken)
    {
        var rows = new List<JournalAttachmentRow>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var name = AttachmentExtractor.CleanFileName(file.FileName);
            var analysis = AttachmentExtractor.Analyze(name, file.Content, cancellationToken);
            rows.Add(new JournalAttachmentRow
            {
                Id = Guid.NewGuid(),
                Position = index,
                FileName = name,
                ContentType = analysis.ContentType,
                Size = file.Content.Length,
                ExtractionStatus = analysis.Status,
                ExtractedText = analysis.ExtractedText,
                SearchText = NormalizeSearch($"{name}\n{analysis.ExtractedText}")
            });
        }

        return rows;
    }

    private static string[] SearchTerms(JournalEntry entry)
    {
        var seed = entry.Text + " " + string.Join(" ", entry.Attachments.Select(attachment =>
            $"{attachment.FileName} {Clip(attachment.ExtractedText ?? "", 400)}"));
        return SearchWord().Matches(NormalizeSearch(seed)).Select(match => match.Value)
            .Where(word => !CommonWords.Contains(word)).GroupBy(word => word)
            .OrderByDescending(group => group.Count()).Take(8).Select(group => group.Key).ToArray();
    }

    private static int MatchCount(string text, string[] terms)
    {
        var normalized = NormalizeSearch(text);
        return terms.Count(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private static string NormalizeSearch(string text) => string.Concat(text.Normalize(NormalizationForm.FormD)
        .Where(character => char.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        .ToLowerInvariant();

    private static string Clip(string text, int limit)
    {
        if (text.Length <= limit) return text;
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }

    private static void RequireLength(string text, int limit, string label)
    {
        if (text.Length > limit)
        {
            throw new JournalInputException($"{label} puede tener hasta {limit:N0} caracteres.");
        }
    }

    private static JournalInputException EntryUnavailable() => new("La entrada ya no está disponible.");
    private static JournalInputException NoteUnavailable() => new("La notita ya no está disponible.");

    [GeneratedRegex(@"\p{L}{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex SearchWord();

    private sealed record EntryProjection(Guid Id, string Text, DateTime CreatedUtc, string? ReflectionJson,
        bool ForSession, bool IsDateOnly, List<JournalAttachment> Attachments);
}
