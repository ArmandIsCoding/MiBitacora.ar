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
    private readonly Dictionary<Guid, NotebookDraft> _notebookDrafts = [];
    private JournalNotebook SelectedNotebook => _notebooks.FirstOrDefault(n => n.Id == _selectedNotebookId)
        ?? new(null, JournalStore.DefaultNotebookName, 0);
    private bool NotebookControlsBusy => !RendererInfo.IsInteractive || _busy || _uploading || _readingEarlier || _showAudio;
    private IEnumerable<JournalNotebook> FilteredNotebooks => _notebooks.Where(n =>
        Spanish.CompareInfo.IndexOf(n.Name, _notebookSearch.Trim(), System.Globalization.CompareOptions.IgnoreCase
            | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0);
    private bool HasNotebookDraft(Guid? id) => id == _selectedNotebookId ? HasDraft :
        _notebookDrafts.TryGetValue(id ?? Guid.Empty, out var draft) && (!string.IsNullOrWhiteSpace(draft.Text) || draft.Files.Length > 0);
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
        if (!_disposed && IsNotebookLocation && _ready && !_busy && !_uploading && !_readingEarlier)
        {
            var requested = RequestedNotebookId();
            if (requested != _selectedNotebookId) await SwitchNotebookAsync(requested);
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

    private async Task SwitchNotebookAsync(Guid? id)
    {
        if (_busy || _uploading || _readingEarlier || _showAudio) return;
        if (id == _selectedNotebookId) { await CloseDialogAsync(); return; }
        await RunNotebookChangeAsync(async () =>
        {
            var entries = await Store.GetNotebookEntriesAsync(_userId, id, PageSize, cancellationToken: Lifetime);
            if (_disposed || !IsNotebookLocation) return;
            ActivateNotebook(id, entries);
            await CloseDialogAsync();
        });
    }

    private void ActivateNotebook(Guid? id, IReadOnlyList<JournalEntry> entries)
    {
        if (id != _selectedNotebookId)
        {
            _notebookDrafts[_selectedNotebookId ?? Guid.Empty] = new(_draft, _files.ToArray());
            var draft = _notebookDrafts.GetValueOrDefault(id ?? Guid.Empty);
            _draft = draft?.Text ?? "";
            _files.Clear();
            _files.AddRange(draft?.Files ?? []);
            _notebookDrafts.Remove(id ?? Guid.Empty);
            _pickerVersion++;
            _uploadError = null;
            _uploadResults.Clear();
        }
        _selectedNotebookId = id;
        _entries.Clear();
        _entries.AddRange(entries);
        _hasEarlier = entries.Count == PageSize;
        _section = WorkspaceSection.Conversation;
        _scrollToEnd = false;
        _focusComposer = false;
        _focusSection = true;
        _notice = null;
        Navigation.NavigateTo(id is { } value ? $"/bitacora?diario={value:D}" : "/bitacora", replace: true);
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
            await Store.MoveEntryAsync(_userId, entryId, target, Lifetime);
            var entry = _entries.Single(e => e.Id == entryId);
            ReplaceEntry(entry with { NotebookId = target, NotebookName = notebook.Name });
            _entries.RemoveAll(e => e.Id == entryId);
            ChangeNotebookCount(entry.NotebookId, -1);
            ChangeNotebookCount(target, 1);
            _sessionDraft = null;
            if (_entries.Count == 0 && _hasEarlier)
            {
                var entries = await Store.GetNotebookEntriesAsync(_userId, _selectedNotebookId, PageSize, cancellationToken: Lifetime);
                _entries.AddRange(entries);
                _hasEarlier = entries.Count == PageSize;
            }
            await CloseDialogAsync();
            _notice = $"Entrada y respuesta movidas a «{notebook.Name}».";
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
            if (_notebookDrafts.Remove(notebookId, out var draft))
            {
                if (_selectedNotebookId is null) MergeDraft(draft);
                else
                {
                    var general = _notebookDrafts.GetValueOrDefault(Guid.Empty) ?? new("", []);
                    _notebookDrafts[Guid.Empty] = new(JoinDrafts(general.Text, draft.Text), general.Files.Concat(draft.Files).ToArray());
                }
            }
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

    private sealed record NotebookDraft(string Text, PendingAttachment[] Files);
}
