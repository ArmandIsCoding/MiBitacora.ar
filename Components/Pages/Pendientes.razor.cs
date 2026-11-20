using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MiBitacora.Services;

namespace MiBitacora.Components.Pages;

public partial class Pendientes
{
    [Inject] private TaskBoardService Tasks { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeZoneInfo Zone { get; set; } = default!;
    [Inject] private ILogger<Pendientes> Logger { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "tablero")] public string? BoardQuery { get; set; }
    [SupplyParameterFromQuery(Name = "tarjeta")] public string? CardQuery { get; set; }
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));
    private string? _userId, _error, _notice, _editor, _dialogError;
    private string _search = "", _draftTitle = "", _draftDescription = "", _draftColor = "sage";
    private bool _busy, _loading = true, _archivedBoards, _showArchivedCards, _draftContext = true, _draftDone, _dialogPending;
    private bool ControlsBusy => _busy || !RendererInfo.IsInteractive;
    private bool BoardLocked => ControlsBusy || _board?.Summary.Archived == true;
    private bool ReadOnly => BoardLocked || _showArchivedCards;
    private bool EditorLocked => _busy || _editor != "board" && _board?.Summary.Archived == true;
    private IReadOnlyList<TaskBoardSummary> _boards = [];
    private TaskBoard? _board;
    private Guid? _editingId;
    private Guid _draftColumn, _editingBoardId;
    private DateOnly? _draftDue;
    private int _editingRevision, _loadVersion;
    private ElementReference _host, _dialog;
    private IJSObjectReference? _module, _drag;
    private DotNetObjectReference<Pendientes>? _bridge;
    private readonly CancellationTokenSource _lifetime = new();
    private IEnumerable<TaskBoardSummary> VisibleBoards => _boards.Where(b => b.Archived == _archivedBoards);
    private IEnumerable<TaskCard> MatchingCards => (_board?.Cards ?? []).Where(c => _search.Length == 0 ||
        (c.Title + "\n" + c.Description).Contains(_search.Trim(), StringComparison.CurrentCultureIgnoreCase));
    private string EditorTitle => _editor switch { "card" => _editingId is null ? "Una tarjeta, un paso." : "Tu tarjeta.", "column" => _editingId is null ? "Un estado a tu medida." : "Editá este estado.", _ => _editingId is null ? "Un tablero para empezar." : "Tu tablero." };
    protected override async Task OnParametersSetAsync()
    {
        _userId ??= (await Auth.GetAuthenticationStateAsync()).User.FindFirstValue(ClaimTypes.NameIdentifier);
        await LoadAsync(true);
    }
    private async Task LoadAsync(bool navigation = false)
    {
        if (_userId is null) return;
        var load = ++_loadVersion; _loading = true;
        try
        {
            var boards = await Tasks.ListAsync(_userId, _lifetime.Token);
            Guid? id = null; Guid? cardId = null;
            if (!string.IsNullOrEmpty(CardQuery))
            {
                if (Guid.TryParse(CardQuery, out var parsed)) { cardId = parsed; id = await Tasks.FindCardBoardAsync(_userId, parsed, _lifetime.Token); }
                if (id is null) _error = "Esa tarjeta ya no está disponible en tu cuenta.";
            }
            else if (!string.IsNullOrEmpty(BoardQuery))
            {
                if (Guid.TryParse(BoardQuery, out var parsed) && boards.Any(b => b.Id == parsed)) id = parsed;
                else _error = "Ese tablero no está disponible en tu cuenta.";
            }
            else id = boards.FirstOrDefault(b => !b.Archived)?.Id;
            var board = id is { } bid ? await Tasks.GetAsync(_userId, bid, _lifetime.Token) : null;
            if (load != _loadVersion) return;
            if (navigation) { _editor = null; _dialogError = null; _search = ""; _showArchivedCards = false; }
            _boards = boards; _board = board;
            if (board is not null) _archivedBoards = board.Summary.Archived;
            if (navigation && cardId is { } cid && board?.Cards.FirstOrDefault(c => c.Id == cid) is { } card)
            { _showArchivedCards = card.Archived; EditCard(card.ColumnId, card); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _error = "No pudimos abrir los tableros. Tus tarjetas se conservan; volvé a intentar."; Logger.LogWarning("No se pudo leer el espacio de pendientes."); }
        finally { if (load == _loadVersion) _loading = false; }
    }
    private async Task ReloadAsync() { _error = null; await CloseEditorAsync(); await LoadAsync(); }
    private void SelectBoard(Guid id) { if (!_busy) { _error = _notice = null; Navigation.NavigateTo($"/pendientes?tablero={id:D}"); } }
    private void BeginEditor(string kind, Guid? id)
    {
        if (ControlsBusy) return;
        _editor = kind; _editingId = id; _dialogError = _error = null; _dialogPending = true;
        _editingBoardId = _board?.Summary.Id ?? Guid.Empty; _editingRevision = _board?.Summary.Revision ?? 0;
    }
    private void EditBoard(TaskBoardSummary? board = null, string? suggested = null)
    {
        BeginEditor("board", board?.Id); _draftTitle = board?.Name ?? suggested ?? ""; _draftDescription = board?.Description ?? "";
        _draftColor = board?.Color ?? (suggested == "Proyectos DIY" ? "clay" : "sage"); _draftContext = board?.IncludeInContext ?? true;
    }
    private void EditColumn(TaskColumn? column = null)
    {
        if (ReadOnly) return; BeginEditor("column", column?.Id); _draftTitle = column?.Name ?? ""; _draftDone = column?.IsDone ?? false;
    }
    private void EditCard(Guid column, TaskCard? card = null)
    {
        BeginEditor("card", card?.Id); _draftTitle = card?.Title ?? ""; _draftDescription = card?.Description ?? "";
        _draftColor = card?.Color ?? "paper"; _draftDue = card?.DueDate; _draftColumn = column;
    }
    private async Task CloseEditorAsync()
    {
        if (_busy) return;
        if (_module is not null && _editor is not null) await _module.InvokeVoidAsync("closeDialog", _dialog);
        _editor = null; _dialogPending = false; _dialogError = null;
    }
    private async Task WorkAsync(Func<Task<TaskBoard>> action, string notice, bool close = false)
    {
        if (_busy || _userId is null) return;
        _busy = true; _error = _dialogError = null;
        try
        {
            var createdBoard = _editor == "board" && _editingId is null;
            var result = await action();
            if (_board is null || _board.Summary.Id == result.Summary.Id || _editor == "board" && _editingId is null) _board = result;
            _boards = await Tasks.ListAsync(_userId, _lifetime.Token); _notice = notice;
            if (close) { if (_module is not null) await _module.InvokeVoidAsync("closeDialog", _dialog); _editor = null; _dialogPending = false; }
            if (_board?.Summary.Id == result.Summary.Id) _archivedBoards = result.Summary.Archived;
            if ((createdBoard || string.IsNullOrEmpty(BoardQuery) && string.IsNullOrEmpty(CardQuery)) && result.Summary.Id == _board?.Summary.Id)
                Navigation.NavigateTo($"/pendientes?tablero={result.Summary.Id:D}", replace: true);
        }
        catch (JournalInputException ex) { _error = _dialogError = ex.Message; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _error = _dialogError = "No pudimos guardar el cambio. Conservamos lo que escribiste; recargá el tablero antes de reintentar."; Logger.LogWarning("No se pudo guardar un cambio de pendientes."); }
        finally { _busy = false; }
    }
    private Task SaveEditorAsync()
    {
        if (_editor == "board") return WorkAsync(() => Tasks.SaveBoardAsync(_userId!, _editingId, _draftTitle, _draftDescription, _draftColor, _draftContext, _editingId is null ? null : _editingRevision, _lifetime.Token), "Tablero guardado. Podés empezar por una tarjeta.", true);
        if (_editor == "column") return WorkAsync(() => Tasks.SaveColumnAsync(_userId!, _editingBoardId, _editingId, _draftTitle, _draftDone, _editingRevision, _lifetime.Token), "Estado guardado.", true);
        return WorkAsync(() => Tasks.SaveCardAsync(_userId!, _editingBoardId, _editingId, _draftColumn, _draftTitle, _draftDescription, _draftColor, _draftDue, _editingRevision, _lifetime.Token), "Tarjeta guardada.", true);
    }
    private Task ArchiveBoardAsync(bool archived) => WorkAsync(() => Tasks.ArchiveBoardAsync(_userId!, _board!.Summary.Id, archived, _board.Summary.Revision, _lifetime.Token), archived ? "Tablero archivado y fuera del contexto. Podés restaurarlo desde Tableros archivados." : "Tablero restaurado.", _editor is not null);
    private Task ArchiveCardAsync(TaskCard card, bool archived) => WorkAsync(() => Tasks.ArchiveCardAsync(_userId!, _board!.Summary.Id, card.Id, archived, _board.Summary.Revision, _lifetime.Token), archived ? "Tarjeta archivada y fuera del contexto. Podés restaurarla cuando quieras." : "Tarjeta restaurada.", _editor is not null);
    private Task ArchiveFromEditorAsync() => _editor == "board" ? ArchiveBoardAsync(!_board!.Summary.Archived) : ArchiveCardAsync(_board!.Cards.Single(c => c.Id == _editingId), !_board.Cards.Single(c => c.Id == _editingId).Archived);
    private Task ShiftColumnAsync(Guid id, int direction) => WorkAsync(() => Tasks.ShiftColumnAsync(_userId!, _editingBoardId, id, direction, _editingRevision, _lifetime.Token), "Orden de estados actualizado.", true);
    private Task RemoveColumnAsync(Guid id) => WorkAsync(() => Tasks.DeleteColumnAsync(_userId!, _editingBoardId, id, _editingRevision, _lifetime.Token), "Estado vacío retirado.", true);
    private Task ChangeStateAsync(TaskCard card, string? value) => Guid.TryParse(value, out var column) ? MoveAsync(card.Id, column, null, _board!.Summary.Revision) : Task.CompletedTask;
    private Task ShiftCardAsync(TaskCard card, int direction)
    {
        var cards = _board!.Cards.Where(c => c.ColumnId == card.ColumnId && !c.Archived).ToArray();
        var index = Array.FindIndex(cards, c => c.Id == card.Id);
        if (index < 0 || index + direction < 0 || index + direction >= cards.Length) return Task.CompletedTask;
        Guid? before = direction < 0 ? cards[index - 1].Id : index + 2 < cards.Length ? cards[index + 2].Id : null;
        return MoveAsync(card.Id, card.ColumnId, before, _board.Summary.Revision);
    }
    private Task MoveAsync(Guid card, Guid column, Guid? before, int revision)
    {
        if (ReadOnly || _board is null) return Task.CompletedTask;
        var state = _board.Columns.FirstOrDefault(c => c.Id == column)?.Name ?? "el estado elegido";
        return WorkAsync(() => Tasks.MoveCardAsync(_userId!, _board.Summary.Id, card, column, before, revision, _lifetime.Token), $"Tarjeta movida a {state}.");
    }
    [JSInvokable] public Task MoveTaskCard(string card, string column, string? before, int revision)
    {
        if (!Guid.TryParse(card, out var cid) || !Guid.TryParse(column, out var col) || before is not null && !Guid.TryParse(before, out _)) return Task.CompletedTask;
        return MoveAsync(cid, col, before is null ? null : Guid.Parse(before), revision);
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./task-boards.js"); _bridge = DotNetObjectReference.Create(this);
            _drag = await _module.InvokeAsync<IJSObjectReference>("create", _host, _bridge);
        }
        if (_dialogPending && _module is not null) { _dialogPending = false; await _module.InvokeVoidAsync("openDialog", _dialog); }
    }
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    private static string ColorName(string value) => value switch { "paper" => "Papel", "sage" => "Verde salvia", "honey" => "Miel", "rose" => "Rosa", "sky" => "Celeste", "lavender" => "Lavanda", _ => "Terracota" };
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { if (_drag is not null) { await _drag.InvokeVoidAsync("dispose"); await _drag.DisposeAsync(); } if (_module is not null) { if (_editor is not null) await _module.InvokeVoidAsync("closeDialog", _dialog); await _module.DisposeAsync(); } }
        catch (JSDisconnectedException) { } catch (TaskCanceledException) { }
        _bridge?.Dispose(); _lifetime.Dispose();
    }
}
