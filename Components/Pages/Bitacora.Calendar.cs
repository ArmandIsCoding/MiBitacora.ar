using Microsoft.AspNetCore.Components;
using MiBitacora.Services;

namespace MiBitacora.Components.Pages;

public partial class Bitacora
{
    [Inject] private TimeZoneInfo JournalTimeZone { get; set; } = default!;

    private IReadOnlyDictionary<DateOnly, int> _entryDays = new Dictionary<DateOnly, int>();
    private DateOnly _today;
    private int _currentStreak;
    private int _calendarRead;
    private Task? _calendarDayWatcher;

    private string StreakLabel => _currentStreak == 1 ? "1 día" : $"{_currentStreak} días";

    private async Task OpenCalendarAsync()
    {
        ShowSection(WorkspaceSection.Calendar);
        _error = null;
        await RefreshEntryDaysAsync();
    }

    private async Task RefreshEntryDaysAsync()
    {
        var read = ++_calendarRead;
        try
        {
            var days = await Store.GetEntryDaysAsync(_userId, JournalTimeZone, Lifetime);
            if (read == _calendarRead && !_disposed)
            {
                _entryDays = days;
                UpdateCalendarDay();
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex) when (!_disposed)
        {
            Logger.LogWarning(ex, "No se pudo actualizar el calendario de la bitácora.");
            if (_section == WorkspaceSection.Calendar)
            {
                _error = "No pudimos actualizar el calendario. Volvé a tocar Calendario para reintentar.";
            }
        }
    }

    private Task<IReadOnlyList<JournalEntry>> LoadCalendarEntriesAsync(DateOnly day) =>
        Store.GetEntriesInRangeAsync(_userId, DayStartUtc(day), DayStartUtc(day.AddDays(1)), Lifetime);

    private void UpdateCalendarDay()
    {
        _today = EntryDay(DateTime.UtcNow);
        var day = _entryDays.ContainsKey(_today) ? _today : _today.AddDays(-1);
        _currentStreak = 0;
        while (_entryDays.ContainsKey(day))
        {
            _currentStreak++;
            day = day.AddDays(-1);
        }
    }

    private async Task WatchCalendarDayAsync()
    {
        try
        {
            while (!_disposed)
            {
                var now = DateTime.UtcNow;
                var nextDay = DayStartUtc(EntryDay(now).AddDays(1));
                var delay = nextDay - now;
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1), Lifetime);
                await InvokeAsync(() =>
                {
                    if (!_disposed)
                    {
                        UpdateCalendarDay();
                        StateHasChanged();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
    }

    private DateOnly EntryDay(DateTime utc) => JournalDates.ToLocalDate(utc, JournalTimeZone);

    private DateTime JournalLocalTime(DateTime utc) => JournalDates.ToLocalTime(utc, JournalTimeZone);

    private DateTime DayStartUtc(DateOnly day) => JournalDates.StartOfDayUtc(day, JournalTimeZone);
}
