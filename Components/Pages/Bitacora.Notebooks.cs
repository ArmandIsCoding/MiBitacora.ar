using Microsoft.AspNetCore.Components;
using MiBitacora.Services;

namespace MiBitacora.Components.Pages;

public partial class Bitacora
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "diario")] public string? InitialNotebook { get; set; }
    private IReadOnlyList<JournalNotebook> _notebooks = [];
    private Guid? _selectedNotebookId;
    private Guid? _editingNotebookId;
    private string _notebookNameDraft = "";
    private string _notebookSearch = "";
    private string _moveNotebookValue = "";
    private readonly Dictionary<(Guid Notebook, Guid Thread), NotebookDraft> _notebookDrafts = [];
    private JournalNotebook SelectedNotebook => _notebooks.FirstOrDefault(n => n.Id == _selectedNotebookId)
        ?? new(null, JournalStore.DefaultNotebookName, 0);
    private bool NotebookControlsBusy => !RendererInfo.IsInteractive || _busy || _uploading || _readingEarlier || _showAudio;
    private IEnumerable<JournalNotebook> FilteredNotebooks => _notebooks.Where(n =>
        Spanish.CompareInfo.IndexOf(n.Name, _notebookSearch.Trim(), System.Globalization.CompareOptions.IgnoreCase
            | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0);
    private bool HasNotebookDraft(Guid? id) => (id == _selectedNotebookId && HasDraft) ||
        _notebookDrafts.Any(pair => pair.Key.Notebook == (id ?? Guid.Empty) && HasDraftContent(pair.Value));
    private static string EntryCountLabel(int count) => count == 1 ? "1 entrada" : $"{count} entradas";

    private Guid? RequestedNotebookId()
    {
        if (string.IsNullOrEmpty(InitialNotebook)) return null;
        if (Guid.TryParse(InitialNotebook, out var id) && _notebooks.Any(n => n.Id == id)) return id;
        _notice = "Ese diario no está disponible. Abrimos Mi diario para que puedas seguir.";
        return null;
    }

    protected override async Task OnParametersSetAsync()
    {
        // Query parameters also change while enhanced navigation is leaving this page.
        // Do not reopen a notebook over a navigation to account/privacy pages.
        if (!_disposed && IsNotebookLocation && _ready && !_busy && !_uploading && !_readingEarlier && !_normalizeThreadUrl)
        {
            var requested = RequestedNotebookId();
            var thread = RequestedThreadId();
            if (requested != _selectedNotebookId || thread != _threadRoot?.Id)
                await SwitchConversationAsync(requested, thread);
        }
    }

    private bool IsNotebookLocation => Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0]
        .TrimEnd('/').Equals("bitacora", StringComparison.OrdinalIgnoreCase);

    private void OpenNotebooks()
    {
        if (NotebookControlsBusy) return;
        _notebookSearch = "";
        OpenDialog(DialogKind.Notebooks);
    }

    private void EditNotebook(JournalNotebook? notebook = null)
    {
        if (NotebookControlsBusy) return;
        _editingNotebookId = notebook?.Id;
        _notebookNameDraft = notebook?.Name ?? "";
        OpenDialog(DialogKind.EditNotebook);
    }

    private Task SwitchNotebookAsync(Guid? id) => SwitchConversationAsync(id, null);

    private async Task SwitchConversationAsync(Guid? id, Guid? threadId)
    {
        if (_busy || _uploading || _readingEarlier || _showAudio) return;
        if (id == _selectedNotebookId && threadId == _threadRoot?.Id) { await CloseDialogAsync(); return; }
        await RunNotebookChangeAsync(async () =>
        {
            var page = await ReadConversationAsync(id, threadId);
            var threads = await Store.GetThreadsAsync(_userId, id, Lifetime);
            if (_disposed || !IsNotebookLocation) return;
            ActivateNotebook(id, page.Entries, page.Thread);
            await RefreshReplyTargetAsync();
            _threads = threads;
            await CloseDialogAsync();
        });
    }

    private void ActivateNotebook(Guid? id, IReadOnlyList<JournalEntry> entries, JournalThreadPage? thread = null)
    {
        if (id != _selectedNotebookId || thread?.Root.Id != _threadRoot?.Id)
        {
            RememberDraft();
            var key = DraftKey(id, thread?.Root.Id);
            _notebookDrafts.Remove(key, out var draft);
            RestoreDraft(draft);
            _replyTarget = draft?.ReplyTarget ?? (thread is null ? null : entries.LastOrDefault() ?? thread.Root);
            _pickerVersion++;
            _uploadError = null;
            _uploadResults.Clear();
        }
        else _notebookDrafts.Remove(DraftKey(id, thread?.Root.Id));
        _selectedNotebookId = id;
        _threadRoot = thread?.Root;
        if (_threadRoot is null) _replyTarget = null;
        _threads = [];
        _entries.Clear();
        _entries.AddRange(entries);
        _hasEarlier = thread?.HasEarlier ?? entries.Count == PageSize;
        _section = WorkspaceSection.Conversation;
        _scrollToEnd = false;
        _focusComposer = false;
        _focusSection = true;
        _notice = null;
        Navigation.NavigateTo(ConversationUrl(id, _threadRoot?.Id), replace: true);
    }

    private async Task SaveNotebookAsync()
    {
        if (string.IsNullOrWhiteSpace(_notebookNameDraft)) return;
        await RunNotebookChangeAsync(async () =>
        {
            var creating = _editingNotebookId is null;
            var notebook = await Store.SaveNotebookAsync(_userId, _editingNotebookId, _notebookNameDraft, Lifetime);
            _notebooks = await Store.GetNotebooksAsync(_userId, Lifetime);
            if (creating)
                ActivateNotebook(notebook.Id, []);
            else
            {
                for (var i = 0; i < _entries.Count; i++)
                    if (_entries[i].NotebookId == notebook.Id) _entries[i] = _entries[i] with { NotebookName = notebook.Name };
                for (var i = 0; i < _sessionEntries.Count; i++)
                    if (_sessionEntries[i].NotebookId == notebook.Id) _sessionEntries[i] = _sessionEntries[i] with { NotebookName = notebook.Name };
            }
            await CloseDialogAsync();
            _notice = creating ? $"Tu diario «{notebook.Name}» está listo." : "Nombre del diario actualizado.";
        });
    }

    private void StartMoveEntry(JournalEntry entry)
    {
        if (NotebookControlsBusy) return;
        _moveNotebookValue = entry.NotebookId?.ToString("D") ?? "";
        OpenDialog(DialogKind.MoveEntry, entry.Id);
    }

    private async Task MoveEntryAsync()
    {
        if (_targetId is not { } entryId) return;
        if (_moveNotebookValue.Length > 0 && !Guid.TryParse(_moveNotebookValue, out _)) return;
        var target = _moveNotebookValue.Length == 0 ? (Guid?)null : Guid.Parse(_moveNotebookValue);
        var notebook = _notebooks.SingleOrDefault(n => n.Id == target);
        if (notebook is null) return;
        await RunNotebookChangeAsync(async () =>
        {
            var movedThread = await Store.GetThreadAsync(_userId, entryId, cancellationToken: Lifetime);
            await Store.MoveEntryAsync(_userId, entryId, target, Lifetime);
            RememberDraft();
            var key = DraftKey(_selectedNotebookId, movedThread.Root.Id);
            if (_notebookDrafts.Remove(key, out var threadDraft))
                _notebookDrafts[DraftKey(target, movedThread.Root.Id)] = threadDraft with
                { ReplyTarget = threadDraft.ReplyTarget is { } reply ? reply with { NotebookId = target, NotebookName = notebook.Name } : null };
            if (_threadRoot?.Id == movedThread.Root.Id) { _draft = ""; _files.Clear(); }
            var entries = await Store.GetNotebookEntriesAsync(_userId, _selectedNotebookId, PageSize, cancellationToken: Lifetime);
            ActivateNotebook(_selectedNotebookId, entries);
            _notebookDrafts.Remove(key);
            _threads = await Store.GetThreadsAsync(_userId, _selectedNotebookId, Lifetime);
            _notebooks = await Store.GetNotebooksAsync(_userId, Lifetime);
            _sessionEntries.Clear();
            _sessionEntries.AddRange(await Store.GetSessionEntriesAsync(_userId, Lifetime));
            _sessionDraft = null;
            await CloseDialogAsync();
            _notice = $"Conversación y adjuntos movidos a «{notebook.Name}».";
        });
    }

    private async Task DeleteNotebookAsync()
    {
        if (_targetId is not { } notebookId) return;
        await RunNotebookChangeAsync(async () =>
        {
            await Store.DeleteNotebookAsync(_userId, notebookId, Lifetime);
            _notebooks = await Store.GetNotebooksAsync(_userId, Lifetime);
            // Keep an unsent draft reachable even after removing its diary.
            if (_selectedNotebookId == notebookId || _selectedNotebookId is null)
            {
                var entries = await Store.GetNotebookEntriesAsync(_userId, null, PageSize, cancellationToken: Lifetime);
                ActivateNotebook(null, entries);
            }
            foreach (var key in _notebookDrafts.Keys.Where(key => key.Notebook == notebookId).ToArray())
            {
                var draft = _notebookDrafts[key];
                _notebookDrafts.Remove(key);
                var targetKey = DraftKey(null, key.Thread == Guid.Empty ? null : key.Thread);
                var general = _notebookDrafts.GetValueOrDefault(targetKey) ?? new("", []);
                _notebookDrafts[targetKey] = new(JoinDrafts(general.Text, draft.Text), general.Files.Concat(draft.Files).ToArray(),
                    draft.ReplyTarget is { } reply ? reply with { NotebookId = null, NotebookName = JournalStore.DefaultNotebookName } : general.ReplyTarget);
            }
            if (_selectedNotebookId is null && _threadRoot is null && _notebookDrafts.Remove(DraftKey(null, null), out var recovered)) MergeDraft(recovered);
            _threads = await Store.GetThreadsAsync(_userId, _selectedNotebookId, Lifetime);
            for (var i = 0; i < _sessionEntries.Count; i++)
                if (_sessionEntries[i].NotebookId == notebookId)
                    _sessionEntries[i] = _sessionEntries[i] with { NotebookId = null, NotebookName = JournalStore.DefaultNotebookName };
            _sessionDraft = null;
            await CloseDialogAsync();
            _notice = "Diario quitado. Sus entradas y respuestas están en Mi diario.";
        });
    }

    private void MergeDraft(NotebookDraft draft)
    {
        _draft = JoinDrafts(_draft, draft.Text);
        _files.AddRange(draft.Files);
    }
    private static string JoinDrafts(string first, string second) => string.IsNullOrWhiteSpace(first) ? second
        : string.IsNullOrWhiteSpace(second) ? first : first + "\n\n" + second;
    private void ChangeNotebookCount(Guid? id, int change) => _notebooks = _notebooks.Select(n =>
        n.Id == id ? n with { EntryCount = Math.Max(0, n.EntryCount + change) } : n).ToArray();

    private async Task RunNotebookChangeAsync(Func<Task> action)
    {
        if (_busy || _uploading || _readingEarlier) return;
        _busy = true;
        _error = null;
        _dialogError = null;
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "No se pudo actualizar un diario.");
            var message = ex is JournalInputException ? ex.Message : "No pudimos hacer el cambio. Probá otra vez.";
            if (_dialogKind == DialogKind.None) _error = message;
            else _dialogError = message;
        }
        finally { _busy = false; }
    }

    private sealed record NotebookDraft(string Text, PendingAttachment[] Files, JournalEntry? ReplyTarget = null);
}
