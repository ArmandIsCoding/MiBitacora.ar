using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed partial class JournalStore
{
    public async Task<IReadOnlyList<JournalThread>> GetThreadsAsync(string userId, Guid? notebookId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await RequireNotebookAsync(db, userId, notebookId, cancellationToken);
        var links = await ReadThreadLinksAsync(db, userId, notebookId, cancellationToken);
        var roots = new Dictionary<Guid, Guid>();
        var groups = links.Values.Where(link => link.ParentId.HasValue)
            .GroupBy(link => ThreadRootId(links, link.Id, roots)).ToArray();
        var rootIds = groups.Select(group => group.Key).ToArray();
        var entries = await ReadEntriesAsync(db.JournalEntries.Where(row => row.UserId == userId && rootIds.Contains(row.Id)), cancellationToken);
        var byId = entries.ToDictionary(entry => entry.Id);
        return groups.Select(group => new JournalThread(group.Key, ThreadLabel(byId[group.Key]), group.Count(),
                AsUtc(group.Max(link => link.CreatedUtc))))
            .OrderByDescending(thread => thread.UpdatedUtc).ThenBy(thread => thread.EntryId).ToArray();
    }

    public async Task<JournalThreadPage> GetThreadAsync(string userId, Guid entryId, int take = 40,
        DateTime? beforeUtc = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId && row.Id == entryId,
            cancellationToken) ?? throw EntryUnavailable();
        var links = await ReadThreadLinksAsync(db, userId, entry.NotebookId, cancellationToken);
        var rootId = ThreadRootId(links, entryId);
        var ids = ThreadEntryIds(links, rootId);
        var root = (await ReadEntriesAsync(db.JournalEntries.Where(row => row.UserId == userId && row.Id == rootId), cancellationToken)).Single();
        var query = db.JournalEntries.Where(row => row.UserId == userId && ids.Contains(row.Id));
        if (beforeUtc is { } before) query = query.Where(row => row.CreatedUtc < before);
        var limit = take == int.MaxValue ? int.MaxValue - 1 : Math.Clamp(take, 1, 100);
        var page = await ReadEntriesAsync(query.OrderByDescending(row => row.CreatedUtc).Take(limit + 1), cancellationToken);
        var hasEarlier = page.Count > limit;
        if (hasEarlier) page.RemoveAt(page.Count - 1);
        page.Reverse();
        return new(root, page, hasEarlier);
    }

    private static string ThreadLabel(JournalEntry entry) =>
        Clip(entry.Reflection?.OpenQuestions.FirstOrDefault() ??
            (string.IsNullOrWhiteSpace(entry.Text) ? "Conversación con Brisa" : entry.Text), 180);

    private static async Task<List<JournalEntry>> ReadReplyHistoryAsync(ApplicationDbContext db, string userId,
        JournalEntry current, CancellationToken ct)
    {
        var history = new List<JournalEntry>();
        var next = current.ReplyToEntryId;
        var answeredQuestion = current.ReplyToQuestion;
        var before = current.CreatedUtc;
        while (next is { } id && history.Count < 8)
        {
            var parent = (await ReadEntriesAsync(db.JournalEntries.Where(row => row.UserId == userId && row.Id == id
                && row.NotebookId == current.NotebookId && row.CreatedUtc < before), ct)).SingleOrDefault();
            if (parent is null) break;
            // Preserve each question along this branch even after a prior response is regenerated.
            if (answeredQuestion is { Length: > 0 } question && parent.Reflection is { } reflection)
                parent = parent with { Reflection = reflection with { OpenQuestions = [question] } };
            history.Add(parent);
            next = parent.ReplyToEntryId;
            answeredQuestion = parent.ReplyToQuestion;
            before = parent.CreatedUtc;
        }
        history.Reverse();
        return history;
    }

    // Read only links, not whole entries or attachments, to navigate a diary's forest of threads.
    private static async Task<Dictionary<Guid, ThreadLink>> ReadThreadLinksAsync(ApplicationDbContext db, string userId,
        Guid? notebookId, CancellationToken ct) => await db.JournalEntries.AsNoTracking()
        .Where(row => row.UserId == userId && row.NotebookId == notebookId)
        .Select(row => new ThreadLink(row.Id, row.ReplyToEntryId, row.CreatedUtc)).ToDictionaryAsync(row => row.Id, ct);

    private static Guid ThreadRootId(Dictionary<Guid, ThreadLink> links, Guid entryId, Dictionary<Guid, Guid>? cached = null)
    {
        var visited = new HashSet<Guid>();
        var id = entryId;
        while (true)
        {
            if (cached?.TryGetValue(id, out var root) == true) { id = root; break; }
            if (!visited.Add(id) || !links.TryGetValue(id, out var link)) throw EntryUnavailable();
            if (link.ParentId is not { } parent) break;
            id = parent;
        }
        if (cached is not null) foreach (var seen in visited) cached[seen] = id;
        return id;
    }

    private static Guid[] ThreadEntryIds(Dictionary<Guid, ThreadLink> links, Guid rootId)
    {
        var children = links.Values.Where(link => link.ParentId.HasValue).ToLookup(link => link.ParentId!.Value);
        var ids = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(rootId);
        while (pending.TryPop(out var id))
        {
            if (!ids.Add(id)) continue;
            foreach (var child in children[id]) pending.Push(child.Id);
        }
        return ids.ToArray();
    }

    private sealed record ThreadLink(Guid Id, Guid? ParentId, DateTime CreatedUtc);
}
