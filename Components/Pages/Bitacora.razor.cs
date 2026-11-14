using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MiBitacora.Services;

namespace MiBitacora.Components.Pages;

public partial class Bitacora : IAsyncDisposable
{
    private const int PageSize = 30;
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    [Inject] private IJournalStore Store { get; set; } = default!;
    [Inject] private IJournalReflector Reflector { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private ILogger<Bitacora> Logger { get; set; } = default!;
    [Inject] private JournalConversationService Conversation { get; set; } = default!;
    [Inject] private AttachmentPreparationService AttachmentPreparation { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "seccion")] public string? InitialSection { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<JournalEntry> _entries = [];
    private readonly List<JournalNote> _notes = [];
    private readonly List<JournalEntry> _sessionEntries = [];
    private readonly List<PendingAttachment> _files = [];
    private readonly Dictionary<Guid, string> _entryErrors = [];
    private JournalProfile _profile = new("", "", false);
    private string _userId = "";
    private string _draft = "";
    private string _noteDraft = "";
    private string? _sessionDraft;
    private string? _notice;
    private string? _error;
    private string? _dialogError;
    private string? _fatalError;
    private Guid? _noteId;
    private Guid? _workingEntry;
    private Guid? _replyToReveal;
    private string _reflectionProgress = "Brisa está pensando en lo que le contaste…";
    private BrisaActivity _reflectionActivity = BrisaActivity.Reading;
    private readonly Dictionary<Guid, BrisaActivity> _replyActivities = [];
    private Guid? _lastCompletedEntry;
    private DateTime? _lastBrisaResponseUtc;
    private BrisaActivity CurrentBrisaActivity => _workingEntry.HasValue ? _reflectionActivity :
        _replyActivities.Values.Contains(BrisaActivity.Thinking) ? BrisaActivity.Thinking :
        _replyActivities.Count > 0 ? BrisaActivity.Reading :
        _lastBrisaResponseUtc is { } replied && DateTime.UtcNow - replied < TimeSpan.FromSeconds(6)
            ? BrisaActivity.Responding : BrisaActivity.Idle;
    private void ReplyActivityChanged(Guid entryId, BrisaActivity activity)
    {
        if (activity is BrisaActivity.Reading or BrisaActivity.Thinking) _replyActivities[entryId] = activity;
        else
        {
            _replyActivities.Remove(entryId);
            if (activity == BrisaActivity.Responding) _lastBrisaResponseUtc = DateTime.UtcNow;
        }
    }
    private Guid? _targetId;
    private bool _ready;
    private bool _busy;
    private bool _uploading;
    private string? _uploadStatus;
    private string? _uploadError;
    private readonly List<string> _uploadResults = [];
    private bool _showAudio;
    private bool _hasEarlier;
    private bool _readingEarlier;
    private bool _scrollToEnd;
    private bool _focusSection;
    private bool _focusComposer;
    private bool _disposed;
    private int _pickerVersion;
    private int _contextRevision;
    private WorkspaceSection _section = WorkspaceSection.Conversation;
    private DialogKind _dialogKind;
    private ElementReference _dialog;
    private ElementReference _composerInput;
    private ElementReference _sectionHeading;
    private ElementReference _end;
    private IJSObjectReference? _module;

    private bool IsAiEnabled => Reflector.IsConfigured;
    private bool HasDraft => !string.IsNullOrWhiteSpace(_draft) || _files.Count > 0;
    private int SessionCount => _sessionEntries.Count + _notes.Count(note => note.ForSession);
    private string DisplayName => string.IsNullOrWhiteSpace(_profile.DisplayName) ? "Hola" : $"Hola, {_profile.DisplayName}";
    private string Initial => string.IsNullOrWhiteSpace(_profile.DisplayName) ? "M" : _profile.DisplayName.Trim()[..1].ToUpper(Spanish);
    private CancellationToken Lifetime => _lifetime.Token;

    protected override async Task OnInitializedAsync()
    {
        if (InitialSection == "temas") _section = WorkspaceSection.Topics;
        var state = await Auth.GetAuthenticationStateAsync();
        _userId = state.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        if (_userId.Length == 0)
        {
            _fatalError = "Tu sesión terminó. Volvé a ingresar para abrir tu bitácora.";
            return;
        }

        try
        {
            _notebooks = await Store.GetNotebooksAsync(_userId, Lifetime);
            if (Reflector is AccountJournalService accountAi) await accountAi.SelectAccountAsync(_userId, Lifetime);
            _selectedNotebookId = RequestedNotebookId();
            var profile = Store.GetProfileAsync(_userId, Lifetime);
            var entries = Store.GetNotebookEntriesAsync(_userId, _selectedNotebookId, PageSize, cancellationToken: Lifetime);
            var notes = Store.GetNotesAsync(_userId, Lifetime);
            var selected = Store.GetSessionEntriesAsync(_userId, Lifetime);
            var entryDays = Store.GetEntryDaysAsync(_userId, JournalTimeZone, Lifetime);
            var threads = Store.GetThreadsAsync(_userId, _selectedNotebookId, Lifetime);
            await Task.WhenAll(profile, entries, notes, selected, entryDays, threads);

            _profile = await profile;
            _entries.AddRange(await entries);
            _threads = await threads;
            _notes.AddRange(await notes);
            _sessionEntries.AddRange(await selected);
            _entryDays = await entryDays;
            UpdateCalendarDay();
            _hasEarlier = _entries.Count == PageSize;
            if (RequestedThreadId() is { } threadId)
            {
                try
                {
                    var page = await ReadConversationAsync(_selectedNotebookId, threadId);
                    _threadRoot = page.Thread!.Root;
                    _entries.Clear();
                    _entries.AddRange(page.Entries);
                    _hasEarlier = page.Thread.HasEarlier;
                    _replyTarget = InitialReplyTo is { } targetId
                        ? await Store.GetEntryAsync(_userId, targetId, Lifetime) : null;
                    if (_replyTarget is null || (await Store.GetThreadAsync(_userId, _replyTarget.Id, 1, cancellationToken: Lifetime)).Root.Id != _threadRoot.Id)
                        _replyTarget = _entries.LastOrDefault() ?? _threadRoot;
                    _focusComposer = InitialReplyTo.HasValue;
                    _normalizeThreadUrl = threadId != _threadRoot.Id || InitialReplyTo.HasValue;
                }
                catch (JournalInputException)
                {
                    _notice = "Ese hilo no está disponible en este diario. Podés seguir escribiendo acá.";
                    _normalizeThreadUrl = true;
                }
            }
            _ready = true;
            _scrollToEnd = _section == WorkspaceSection.Conversation && _entries.Count > 0;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo abrir la bitácora.");
            _fatalError = "No pudimos abrir tu bitácora. Recargá la página para intentar de nuevo.";
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_normalizeThreadUrl)
        {
            _normalizeThreadUrl = false;
            Navigation.NavigateTo(ConversationUrl(_selectedNotebookId, _threadRoot?.Id), replace: true);
        }
        if (_ready)
        {
            _calendarDayWatcher ??= WatchCalendarDayAsync();
        }

        try
        {
            if (_dialogKind != DialogKind.None)
            {
                var module = await GetModuleAsync();
                if (_dialogKind != DialogKind.None)
                    await module.InvokeVoidAsync("showDialog", Lifetime, _dialog);
            }

            if (_focusSection)
            {
                _focusSection = false;
                var module = await GetModuleAsync();
                await module.InvokeVoidAsync("focusSection", Lifetime, _sectionHeading);
            }

            if (_focusComposer && _section == WorkspaceSection.Conversation)
            {
                _focusComposer = false;
                var module = await GetModuleAsync();
                await module.InvokeVoidAsync("focusComposer", Lifetime, _composerInput);
            }

            if (_scrollToEnd && _section == WorkspaceSection.Conversation)
            {
                _scrollToEnd = false;
                var module = await GetModuleAsync();
                await module.InvokeVoidAsync("reveal", Lifetime, _end);
            }
            if (_replyToReveal is { } ready)
            {
                _replyToReveal = null;
                if (_section == WorkspaceSection.Conversation && _dialogKind == DialogKind.None)
                    await (await GetModuleAsync()).InvokeVoidAsync("centerReply", Lifetime, $"reply-{ready:N}");
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
    }

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await Js.InvokeAsync<IJSObjectReference>("import", Lifetime, "./" + Assets["journal.js"]);

    private void ShowSection(WorkspaceSection section)
    {
        if (_showAudio) return;
        if (_section == section)
        {
            _focusSection = true;
            return;
        }

        _section = section;
        _scrollToEnd = false;
        _focusComposer = false;
        _focusSection = true;
        _notice = null;
        _error = null;
    }

    private async Task LoadEarlierAsync()
    {
        if (_readingEarlier || _busy || _entries.Count == 0)
        {
            return;
        }

        _readingEarlier = true;
        try
        {
            var threadPage = _threadRoot is { } root
                ? await Store.GetThreadAsync(_userId, root.Id, PageSize, _entries[0].CreatedUtc, Lifetime) : null;
            var earlier = threadPage?.Entries ?? await Store.GetNotebookEntriesAsync(_userId, _selectedNotebookId, PageSize, _entries[0].CreatedUtc, Lifetime);
            _entries.InsertRange(0, earlier);
            _hasEarlier = threadPage?.HasEarlier ?? earlier.Count == PageSize;
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "No se pudo cargar el historial.");
            _error = "No pudimos cargar las entradas anteriores. Probá otra vez.";
        }
        finally
        {
            _readingEarlier = false;
        }
    }

    private void AddExample()
    {
        if (!string.IsNullOrWhiteSpace(_draft))
        {
            return;
        }

        _draft = "Esta semana acepté tres tareas más en el trabajo. Me entusiasma el proyecto y al mismo tiempo me cuesta decir que no. El jueves cancelé un plan que tenía ganas de hacer y me quedé pensando si puedo poner un límite sin quedar mal. Quiero hablar de esto en mi próxima sesión.";
        FocusComposer();
    }

    private void FocusComposer()
    {
        ShowSection(WorkspaceSection.Conversation);
        _focusSection = false;
        _scrollToEnd = false;
        _focusComposer = true;
    }

    private async Task PickFilesAsync(InputFileChangeEventArgs args)
    {
        if (_uploading || _busy)
        {
            return;
        }

        _error = null;
        _uploading = true;
        _uploadError = null;
        _uploadResults.Clear();
        var skipped = new List<string>();
        try
        {
            foreach (var file in args.GetMultipleFiles(args.FileCount))
            {
                try
                {
                    if (_files.Count >= 30) throw new JournalInputException("Podés adjuntar hasta 30 archivos por mensaje.");
                    if (file.Size is <= 0 or > AttachmentPreparationService.MaxIncomingBytes)
                        throw new JournalInputException("El archivo puede pesar hasta 256 MB antes de optimizarse.");
                    _uploadStatus = $"Recibiendo {file.Name}…";
                    await InvokeAsync(StateHasChanged);
                    var progress = new Progress<string>(message =>
                    {
                        if (!_disposed) _ = InvokeAsync(() => { _uploadStatus = message; StateHasChanged(); });
                    });
                    await using var source = file.OpenReadStream(AttachmentPreparationService.MaxIncomingBytes, Lifetime);
                    var prepared = await AttachmentPreparation.PrepareAsync(file.Name, file.Size, source,
                        !Reflector.UsesExternalProvider, progress, Lifetime);
                    if (_files.Sum(item => (long)item.Content.Length) + prepared.Attachment.Content.Length > JournalStore.MaxEntryAttachmentBytes)
                        throw new JournalInputException("Los adjuntos optimizados juntos pueden pesar hasta 128 MB por mensaje. Quitá alguno o guardalo en otra entrada.");
                    _files.Add(prepared.Attachment);
                    _uploadResults.Add(prepared.Optimized
                        ? $"{file.Name}: listo · {AttachmentPresentation.SizeLabel(prepared.OriginalBytes)} → {AttachmentPresentation.SizeLabel(prepared.Attachment.Content.Length)}"
                        : $"{file.Name}: adjuntado");
                }
                catch (JournalInputException ex) { skipped.Add($"{file.Name}: {ex.Message}"); }
                catch (Exception ex) when (!_disposed && ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Logger.LogWarning(ex, "No se pudo preparar un adjunto.");
                    skipped.Add($"{file.Name}: no pudimos recibirlo. Volvé a seleccionarlo e intentá otra vez.");
                }
            }

            if (skipped.Count > 0)
            {
                _uploadError = string.Join(" ", skipped);
            }
        }
        finally
        {
            _uploading = false;
            _uploadStatus = null;
            _pickerVersion++;
        }
    }

    private void RemoveFile(PendingAttachment file)
    {
        _files.Remove(file);
        _uploadResults.Clear();
    }

    private async Task BeginReflectionAsync(Guid? entryId = null)
    {
        if (_busy || _uploading || _showAudio || (entryId is null && !HasDraft))
        {
            return;
        }

        _error = null;
        _busy = true;
        try
        {
            _profile = await Store.GetProfileAsync(_userId, Lifetime);
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "No se pudo leer la configuración del cuaderno.");
            _error = "No pudimos leer tu configuración. Probá otra vez.";
            return;
        }
        finally
        {
            _busy = false;
        }

        await SaveOrReflectAsync(entryId, organize: true, useAi: IsAiEnabled);
    }

    private async Task SaveOrReflectAsync(Guid? entryId, bool organize, bool useAi)
    {
        if (_busy || _uploading || _showAudio || (entryId is null && !HasDraft))
        {
            return;
        }

        var newEntry = entryId is null;
        var completed = false;
        _scrollToEnd = false;
        _busy = true;
        _notice = null;
        _error = null;
        try
        {
            if (entryId is null)
            {
                var entry = await Store.AddEntryAsync(_userId, _draft, _files.ToArray(), Lifetime, _selectedNotebookId,
                    replyToEntryId: _threadRoot is null ? null : _replyTarget?.Id ?? _threadRoot.Id);
                _entries.Add(entry);
                if (_threadRoot is not null) _replyTarget = entry;
                ChangeNotebookCount(_selectedNotebookId, 1);
                _draft = "";
                _files.Clear();
                _uploadResults.Clear();
                _uploadError = null;
                entryId = entry.Id;
                _threads = await Store.GetThreadsAsync(_userId, _selectedNotebookId, Lifetime);
                await RefreshEntryDaysAsync();
            }

            _scrollToEnd = newEntry;
            if (!organize)
            {
                _notice = "Entrada guardada.";
                return;
            }

            _workingEntry = entryId;
            _reflectionActivity = BrisaActivity.Reading;
            _reflectionProgress = "Brisa está retomando el hilo de la conversación…";
            _entryErrors.Remove(entryId.Value);
            await InvokeAsync(StateHasChanged);

            var generatingEntry = entryId.Value;
            var updated = await Conversation.GenerateAsync(_userId, generatingEntry, useAi, new MemoryProgress(message =>
                {
                    if (!_disposed) _ = InvokeAsync(() => { if (_workingEntry == generatingEntry) { _reflectionProgress = message; StateHasChanged(); } });
                }), Lifetime, new ActivityProgress(activity =>
                {
                    if (!_disposed) _ = InvokeAsync(() => { if (_workingEntry == generatingEntry) { _reflectionActivity = activity; StateHasChanged(); } });
                }));
            ReplaceEntry(updated);
            completed = true;
            _lastCompletedEntry = generatingEntry;
            _lastBrisaResponseUtc = DateTime.UtcNow;
            if (_section == WorkspaceSection.Calendar)
            {
                await RefreshEntryDaysAsync();
            }

            _sessionDraft = null;
        }
        catch (JournalInputException ex)
        {
            _error = ex.Message;
        }
        catch (JournalReflectionException ex)
        {
            if (entryId is Guid saved)
            {
                _entryErrors[saved] = ex.Message;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo completar una operación de la bitácora.");
            if (entryId is Guid saved)
            {
                _entryErrors[saved] = "Tu entrada quedó guardada. No pudimos armar la devolución en este momento.";
            }
            else
            {
                _error = "No pudimos guardar la entrada. Tu borrador sigue acá para que vuelvas a intentar.";
            }
        }
        finally
        {
            if (!_disposed && entryId.HasValue)
            {
                // A transcript may have succeeded even if memory or response generation failed.
                try { if (await Store.GetEntryAsync(_userId, entryId.Value, Lifetime) is { } saved) ReplaceEntry(saved); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogWarning("No se pudo refrescar la entrada después de generar."); }
            }
            _workingEntry = null;
            _busy = false;
            if (completed) { _scrollToEnd = false; _replyToReveal = entryId; }
        }
    }

    private Task AddVoiceAttachmentAsync(PendingAttachment file)
    {
        if (file.Content.Length is <= 0 or > JournalStore.MaxFileBytes ||
            _files.Sum(a => (long)a.Content.Length) + file.Content.Length > JournalStore.MaxEntryAttachmentBytes)
            throw new JournalInputException("El audio supera el espacio disponible: 64 MB por archivo y 128 MB por entrada.");
        _files.Add(file);
        _showAudio = false;
        return Task.CompletedTask;
    }

    private void ReplyUpdated(JournalEntry entry) { ReplaceEntry(entry); _sessionDraft = null; }
    private void EntryEdited(JournalEntry entry)
    {
        ReplaceEntry(entry);
        _entryErrors.Remove(entry.Id);
        _contextRevision++;
        _sessionDraft = null;
        _notice = "Entrada actualizada. Podés pedir una nueva respuesta.";
    }

    private void ReplaceEntry(JournalEntry entry)
    {
        if (_replyTarget?.Id == entry.Id) _replyTarget = entry;
        if (_threadRoot?.Id == entry.Id) _threadRoot = entry;
        var index = _entries.FindIndex(item => item.Id == entry.Id);
        if (index >= 0)
        {
            _entries[index] = entry;
        }

        _sessionEntries.RemoveAll(item => item.Id == entry.Id);
        if (entry.ForSession)
        {
            _sessionEntries.Add(entry);
            _sessionEntries.Sort((left, right) => left.CreatedUtc.CompareTo(right.CreatedUtc));
        }
    }

    private async Task ToggleEntryAsync(JournalEntry entry)
    {
        await RunChangeAsync(async () =>
        {
            var selected = !entry.ForSession;
            await Store.SetEntryForSessionAsync(_userId, entry.Id, selected, Lifetime);
            ReplaceEntry(entry with { ForSession = selected });
            _sessionDraft = null;
            _notice = selected ? "Entrada marcada para retomar." : "Entrada quitada de tu hoja.";
        });
    }

    private async Task ToggleNoteAsync(JournalNote note)
    {
        await RunChangeAsync(async () =>
        {
            var selected = !note.ForSession;
            await Store.SetNoteForSessionAsync(_userId, note.Id, selected, Lifetime);
            _notes[_notes.FindIndex(item => item.Id == note.Id)] = note with { ForSession = selected };
            _sessionDraft = null;
            _notice = selected ? "Notita marcada para retomar." : "Notita quitada de tu hoja.";
        });
    }

    private void StartNote(JournalNote? note = null, string? initialText = null)
    {
        _noteDraft = note?.Text ?? initialText ?? "";
        _noteId = note?.Id;
        OpenDialog(DialogKind.Note);
    }

    private async Task SaveNoteAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_noteDraft))
        {
            return;
        }

        _busy = true;
        _dialogError = null;
        try
        {
            var note = await Store.SaveNoteAsync(_userId, _noteId, _noteDraft, Lifetime);
            _notes.RemoveAll(item => item.Id == note.Id);
            _notes.Insert(0, note);
            _contextRevision++;
            _sessionDraft = null;
            await CloseDialogAsync();
            _notice = "Notita guardada. Ya forma parte de tu cuaderno.";
        }
        catch (JournalInputException ex)
        {
            _dialogError = ex.Message;
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "No se pudo guardar una notita.");
            _dialogError = "No pudimos guardar la notita. Probá otra vez.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_targetId is not Guid id)
        {
            return;
        }

        var kind = _dialogKind;
        await RunChangeAsync(async () =>
        {
            if (kind == DialogKind.DeleteNote)
            {
                await Store.DeleteNoteAsync(_userId, id, Lifetime);
                _notes.RemoveAll(item => item.Id == id);
                _contextRevision++;
                _notice = "Notita eliminada.";
            }
            else
            {
                await Store.DeleteEntryAsync(_userId, id, Lifetime);
                if (_threadRoot?.Id != id && _notebookDrafts.Remove(DraftKey(_selectedNotebookId, id), out var orphan))
                {
                    if (_threadRoot is null) MergeDraft(orphan);
                    else
                    {
                        var key = DraftKey(_selectedNotebookId, null);
                        var general = _notebookDrafts.GetValueOrDefault(key) ?? new("", []);
                        _notebookDrafts[key] = new(JoinDrafts(general.Text, orphan.Text), general.Files.Concat(orphan.Files).ToArray());
                    }
                }
                ChangeNotebookCount(_entries.FirstOrDefault(entry => entry.Id == id)?.NotebookId, -1);
                _entries.RemoveAll(item => item.Id == id);
                _sessionEntries.RemoveAll(item => item.Id == id);
                _entryErrors.Remove(id);
                await ReloadConversationAsync();
                _notice = "Entrada y adjuntos eliminados.";
                await RefreshEntryDaysAsync();
            }

            _sessionDraft = null;
            await CloseDialogAsync();
        });
    }

    private async Task RunChangeAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _error = null;
        _dialogError = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "No se pudo actualizar la bitácora.");
            if (_dialogKind == DialogKind.None)
            {
                _error = "No pudimos hacer el cambio. Probá otra vez.";
            }
            else
            {
                _dialogError = "No pudimos hacer el cambio. Probá otra vez.";
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenDialog(DialogKind kind, Guid? target = null)
    {
        _dialogKind = kind;
        _targetId = target;
        _dialogError = null;
    }

    private Task CloseDialogAsync()
    {
        // Removing the dialog also removes its native modal backdrop. Change the state before
        // yielding so a render during a save/delete cannot reopen an empty dialog after close().
        _dialogKind = DialogKind.None;
        _targetId = null;
        _dialogError = null;
        return Task.CompletedTask;
    }

    private Task OnDialogKeyAsync(KeyboardEventArgs args) =>
        args.Key == "Escape" && !_busy ? CloseDialogAsync() : Task.CompletedTask;

    private string BuildSessionSheet()
    {
        var sheet = new StringBuilder();
        sheet.AppendLine("Mi Bitácora · Ideas para retomar");
        foreach (var entry in _sessionEntries)
        {
            sheet.AppendLine().AppendLine($"{LocalDate(entry.CreatedUtc)} · {entry.Reflection?.Title ?? "Entrada"}");
            if (entry.Reflection is { } reflection)
            {
                sheet.AppendLine(reflection.Overview);
                foreach (var topic in reflection.SessionTopics)
                {
                    sheet.AppendLine($"• {topic}");
                }

                if (reflection.OpenQuestions.Count > 0)
                {
                    sheet.AppendLine("Preguntas que quedaron abiertas");
                    foreach (var question in reflection.OpenQuestions)
                    {
                        sheet.AppendLine($"• {question}");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.Text))
            {
                sheet.AppendLine(entry.Text);
            }

            if (entry.Attachments.Count > 0)
            {
                sheet.AppendLine($"Adjuntos: {string.Join(", ", entry.Attachments.Select(file => file.FileName))}");
            }
        }

        foreach (var note in _notes.Where(note => note.ForSession))
        {
            sheet.AppendLine().AppendLine($"Notita · {LocalDate(note.CreatedUtc)}").AppendLine(note.Text);
        }

        return sheet.ToString().TrimEnd();
    }

    private async Task CopySheetAsync()
    {
        try
        {
            var module = await GetModuleAsync();
            var copied = await module.InvokeAsync<bool>("copyText", Lifetime, _sessionDraft ?? BuildSessionSheet());
            _notice = copied ? "Hoja copiada." : "Seleccioná el texto de la hoja para copiarlo.";
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task PrintSheetAsync()
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync("printSheet", Lifetime);
    }

    private string LocalDate(DateTime utc) =>
        JournalLocalTime(utc).ToString("d 'de' MMMM", Spanish);

    private string LocalTime(DateTime utc) => JournalLocalTime(utc).ToString("HH:mm", Spanish);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_calendarDayWatcher is not null)
        {
            await _calendarDayWatcher;
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _lifetime.Dispose();
    }

    private enum WorkspaceSection { Conversation, Calendar, Topics, Notes, Session }
    private sealed class MemoryProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class ActivityProgress(Action<BrisaActivity> report) : IProgress<BrisaActivity> { public void Report(BrisaActivity value) => report(value); }
    private enum DialogKind { None, Note, DeleteNote, DeleteEntry, Notebooks, EditNotebook, DeleteNotebook, MoveEntry }
}
