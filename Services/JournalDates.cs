namespace MiBitacora.Services;

internal static class JournalDates
{
    public static DateTime ToLocalTime(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

    public static DateOnly ToLocalDate(DateTime utc, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(ToLocalTime(utc, timeZone));

    public static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);

        // Some time zones advance their clocks at midnight.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).UtcDateTime;
    }
}
