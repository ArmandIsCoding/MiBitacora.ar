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
    private Guid? _targetId;
    private bool _ready;
    private bool _busy;
    private bool _uploading;
    private bool _hasEarlier;
    private bool _readingEarlier;
    private bool _scrollToEnd;
    private bool _focusSection;
    private bool _focusComposer;
    private bool _disposed;
    private int _pickerVersion;
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
        var state = await Auth.GetAuthenticationStateAsync();
        _userId = state.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        if (_userId.Length == 0)
        {
            _fatalError = "Tu sesión terminó. Volvé a ingresar para abrir tu bitácora.";
            return;
        }

        try
        {
            var profile = Store.GetProfileAsync(_userId, Lifetime);
            var entries = Store.GetEntriesAsync(_userId, PageSize, cancellationToken: Lifetime);
            var notes = Store.GetNotesAsync(_userId, Lifetime);
            var selected = Store.GetSessionEntriesAsync(_userId, Lifetime);
            var entryDays = Store.GetEntryDaysAsync(_userId, JournalTimeZone, Lifetime);
            await Task.WhenAll(profile, entries, notes, selected, entryDays);

            _profile = await profile;
            _entries.AddRange(await entries);
            _notes.AddRange(await notes);
            _sessionEntries.AddRange(await selected);
            _entryDays = await entryDays;
            UpdateCalendarDay();
            _hasEarlier = _entries.Count == PageSize;
            _ready = true;
            _scrollToEnd = _entries.Count > 0;
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
        if (_ready)
        {
            _calendarDayWatcher ??= WatchCalendarDayAsync();
        }

        try
        {
            if (_dialogKind != DialogKind.None)
            {
                var module = await GetModuleAsync();
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
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
    }

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await Js.InvokeAsync<IJSObjectReference>("import", Lifetime, "./journal.js");

    private void ShowSection(WorkspaceSection section)
    {
        if (_section == section)
        {
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
        if (_readingEarlier || _entries.Count == 0)
        {
            return;
        }

        _readingEarlier = true;
        try
        {
            var earlier = await Store.GetEntriesAsync(_userId, PageSize, _entries[0].CreatedUtc, Lifetime);
            _entries.InsertRange(0, earlier);
            _hasEarlier = earlier.Count == PageSize;
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
        var skipped = new List<string>();
        try
        {
            foreach (var file in args.GetMultipleFiles(args.FileCount))
            {
                if (file.Size is <= 0 or > JournalStore.MaxFileBytes
                    || _files.Sum(item => (long)item.Content.Length) + file.Size > JournalStore.MaxEntryAttachmentBytes)
                {
                    skipped.Add(file.Name);
                    continue;
                }

                try
                {
                    await using var source = file.OpenReadStream(JournalStore.MaxFileBytes, Lifetime);
                    using var buffer = new MemoryStream((int)file.Size);
                    await source.CopyToAsync(buffer, Lifetime);
                    _files.Add(new PendingAttachment(file.Name, buffer.ToArray()));
                }
                catch (Exception ex) when (!_disposed && ex is IOException or InvalidOperationException)
                {
                    skipped.Add(file.Name);
                }
            }

            if (skipped.Count > 0)
            {
                _error = $"No se pudieron adjuntar {string.Join(", ", skipped)}. El límite es de 8 MB por archivo y 16 MB por entrada.";
            }
        }
        finally
        {
            _uploading = false;
            _pickerVersion++;
        }
    }

    private void RemoveFile(PendingAttachment file) => _files.Remove(file);

    private async Task BeginReflectionAsync(Guid? entryId = null)
    {
        if (_busy || _uploading || (entryId is null && !HasDraft))
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
        if (_busy || _uploading || (entryId is null && !HasDraft))
        {
            return;
        }

        _busy = true;
        _notice = null;
        _error = null;
        try
        {
            if (entryId is null)
            {
                var entry = await Store.AddEntryAsync(_userId, _draft, _files.ToArray(), Lifetime);
                _entries.Add(entry);
                _draft = "";
                _files.Clear();
                entryId = entry.Id;
                await RefreshEntryDaysAsync();
            }

            _scrollToEnd = true;
            if (!organize)
            {
                _notice = "Entrada guardada.";
                return;
            }

            _workingEntry = entryId;
            _entryErrors.Remove(entryId.Value);
            await InvokeAsync(StateHasChanged);

            var context = await Store.GetContextAsync(_userId, entryId.Value, useAi, Lifetime);
            var reflection = await Reflector.ReflectAsync(context, useAi, Lifetime);
            await Store.SaveReflectionAsync(_userId, entryId.Value, reflection, Lifetime);

            ReplaceEntry(context.Current with { Reflection = reflection });
            if (_section == WorkspaceSection.Calendar)
            {
                await RefreshEntryDaysAsync();
            }

            _sessionDraft = null;
            _scrollToEnd = true;
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
            _workingEntry = null;
            _busy = false;
        }
    }

    private void ReplaceEntry(JournalEntry entry)
    {
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
                _notice = "Notita eliminada.";
            }
            else
            {
                await Store.DeleteEntryAsync(_userId, id, Lifetime);
                _entries.RemoveAll(item => item.Id == id);
                _sessionEntries.RemoveAll(item => item.Id == id);
                _entryErrors.Remove(id);
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

    private async Task CloseDialogAsync()
    {
        try
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("closeDialog", Lifetime, _dialog);
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            _dialogKind = DialogKind.None;
            _targetId = null;
            _dialogError = null;
        }
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

    private static string SizeLabel(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : $"{Math.Max(1, bytes / 1024d):0} KB";

    private static string AttachmentStatus(JournalAttachment file) =>
        HeicImageService.IsHeic(file.ContentType) ? "Vista JPG · original conservado" :
        file.ExtractionStatus.Contains("Gemini", StringComparison.Ordinal)
            ? (AttachmentExtractor.CanAnalyzeLocally(file.ContentType)
                ? "Disponible para analizar" : "Guardado · formato sin análisis visual")
            : file.ExtractionStatus;

    private static bool HasPreview(JournalAttachment file) =>
        HeicImageService.HasPreview(file.ContentType);

    private static string AttachmentUrl(Guid id) => $"/adjuntos/{id}";

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
    private enum DialogKind { None, Note, DeleteNote, DeleteEntry }
}
