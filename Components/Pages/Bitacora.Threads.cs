using Microsoft.AspNetCore.Components;
using MiBitacora.Services;

namespace MiBitacora.Components.Pages;

public partial class Bitacora
{
    [SupplyParameterFromQuery(Name = "hilo")] public string? InitialThread { get; set; }
    [SupplyParameterFromQuery(Name = "responder")] public Guid? InitialReplyTo { get; set; }
    private JournalEntry? _threadRoot;
    private JournalEntry? _replyTarget;
    private IReadOnlyList<JournalThread> _threads = [];
    private bool _normalizeThreadUrl;

    private string ThreadTitle => _threadRoot is { } root ? ThreadTitleFor(root) : "";
    private static string ThreadTitleFor(JournalEntry entry) => entry.Reflection?.OpenQuestions.FirstOrDefault()
        ?? (string.IsNullOrWhiteSpace(entry.Text) ? "Conversación con Brisa" : Preview(entry.Text, 180));
    private string ReplyPrompt => _replyTarget is { } reply ? ThreadTitleFor(reply) : ThreadTitle;
    private static string Preview(string text, int length) => text.Length <= length ? text
        : text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)] + "…";
    private Guid? RequestedThreadId() => Guid.TryParse(InitialThread, out var id) ? id : null;
    private static string ConversationUrl(Guid? notebookId, Guid? threadId)
    {
        var query = new List<string>();
        if (notebookId is { } notebook) query.Add($"diario={notebook:D}");
        if (threadId is { } thread) query.Add($"hilo={thread:D}");
        return "/bitacora" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
    }

    private async Task<(IReadOnlyList<JournalEntry> Entries, JournalThreadPage? Thread)> ReadConversationAsync(Guid? notebookId, Guid? threadId)
    {
        if (threadId is { } id)
        {
            var thread = await Store.GetThreadAsync(_userId, id, PageSize, cancellationToken: Lifetime);
            if (thread.Root.NotebookId != notebookId)
                throw new JournalInputException("Ese hilo no está en este diario. Abrilo desde el diario correspondiente.");
            return (thread.Entries, thread);
        }
        return (await Store.GetNotebookEntriesAsync(_userId, notebookId, PageSize, cancellationToken: Lifetime), null);
    }

    private async Task RespondAsync(JournalEntry entry)
    {
        if (NotebookControlsBusy) return;
        if (_threadRoot is null)
            await SwitchConversationAsync(entry.NotebookId, entry.Id);
        if (_threadRoot is null) return;
        _replyTarget = entry;
        FocusComposer();
    }

    private static (Guid Notebook, Guid Thread) DraftKey(Guid? notebookId, Guid? threadId) =>
        (notebookId ?? Guid.Empty, threadId ?? Guid.Empty);
    private static bool HasDraftContent(NotebookDraft draft) => !string.IsNullOrWhiteSpace(draft.Text) || draft.Files.Length > 0;
    private void RememberDraft() => _notebookDrafts[DraftKey(_selectedNotebookId, _threadRoot?.Id)] = new(_draft, _files.ToArray(), _replyTarget);
    private void RestoreDraft(NotebookDraft? draft)
    {
        _draft = draft?.Text ?? "";
        _files.Clear();
        _files.AddRange(draft?.Files ?? []);
    }

    private async Task ReloadConversationAsync()
    {
        if (_threadRoot is { } root && await Store.GetEntryAsync(_userId, root.Id, Lifetime) is null)
        {
            // Keep an unsent response reachable when the thread's original entry is deleted.
            var unsent = new NotebookDraft(_draft, _files.ToArray());
            _notebookDrafts.Remove(DraftKey(_selectedNotebookId, root.Id));
            _threadRoot = null;
            _replyTarget = null;
            _notebookDrafts.Remove(DraftKey(_selectedNotebookId, null), out var general);
            RestoreDraft(general);
            MergeDraft(unsent);
        }
        var page = await ReadConversationAsync(_selectedNotebookId, _threadRoot?.Id);
        _entries.Clear();
        _entries.AddRange(page.Entries);
        _hasEarlier = page.Thread?.HasEarlier ?? _entries.Count == PageSize;
        _threadRoot = page.Thread?.Root;
        await RefreshReplyTargetAsync();
        _threads = await Store.GetThreadsAsync(_userId, _selectedNotebookId, Lifetime);
        Navigation.NavigateTo(ConversationUrl(_selectedNotebookId, _threadRoot?.Id), replace: true);
    }

    private async Task RefreshReplyTargetAsync()
    {
        if (_threadRoot is null) { _replyTarget = null; return; }
        var target = _replyTarget is { } previous
            ? _entries.FirstOrDefault(entry => entry.Id == previous.Id) ?? await Store.GetEntryAsync(_userId, previous.Id, Lifetime)
            : null;
        if (target is not null && target.NotebookId == _selectedNotebookId &&
            (_entries.Any(entry => entry.Id == target.Id) ||
                (await Store.GetThreadAsync(_userId, target.Id, 1, cancellationToken: Lifetime)).Root.Id == _threadRoot.Id))
            _replyTarget = target;
        else _replyTarget = _entries.LastOrDefault() ?? _threadRoot;
    }
}
