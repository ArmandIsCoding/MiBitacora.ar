using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed record AiUsageSnapshot(int Limit, int Used, int Reserved, DateTime FromUtc, DateTime ThroughUtc)
{
    public int Remaining => Math.Max(0, Limit - Used - Reserved);
    public bool NearLimit => Used + Reserved >= Math.Ceiling(Limit * .8);
}

public sealed class AiUsageService(IDbContextFactory<ApplicationDbContext> factory, SubscriptionService billing,
    IOptions<SubscriptionOptions> options, TimeProvider clock, TimeZoneInfo timeZone)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public async Task<AiUsageSnapshot> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ReadAsync(db, userId, ct);
    }

    private async Task<AiUsageSnapshot> ReadAsync(ApplicationDbContext db, string userId, CancellationToken ct)
    {
        var now = Now;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var first = new DateOnly(local.Year, local.Month, 1);
        var from = JournalDates.StartOfDayUtc(first, timeZone);
        var through = JournalDates.StartOfDayUtc(first.AddMonths(1), timeZone);
        var limit = options.Value.MonthlyCredits;
        var period = await (from payment in db.BillingPayments.AsNoTracking()
                            join subscription in db.BillingSubscriptions.AsNoTracking() on payment.SubscriptionId equals subscription.Id
                            where subscription.UserId == userId && payment.Status == "approved" &&
                                payment.PeriodStartUtc <= now && payment.PeriodEndUtc > now &&
                                (subscription.Provider == "MercadoPago" || (subscription.Provider == "Simulated" && billing.IsSimulation))
                            orderby payment.PeriodStartUtc descending
                            select new { payment.PeriodStartUtc, payment.PeriodEndUtc, subscription.MonthlyCredits }).FirstOrDefaultAsync(ct);
        if (period is not null) { from = period.PeriodStartUtc; through = period.PeriodEndUtc; limit = period.MonthlyCredits; }
        var records = db.AiUsageReservations.Where(row => row.UserId == userId && row.PeriodStartUtc == from);
        var used = await records.Where(row => row.Completed).SumAsync(row => (int?)row.Credits, ct) ?? 0;
        var reserved = await records.Where(row => !row.Completed && row.ExpiresUtc > now).SumAsync(row => (int?)row.Credits, ct) ?? 0;
        return new(limit, used, reserved, from, through);
    }

    public async Task<AiUsageLease> ReserveAsync(string userId, string kind, int credits, CancellationToken ct = default)
    {
        if (kind is not ("reply" or "report" or "audio") || credits < 1) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!(await billing.GetAccessAsync(userId, ct)).CanUseOpenAi)
            throw new JournalInputException("El Plan Profundo ya no está vigente. Podés continuar con IA local desde Mi plan.");
        await using var db = await factory.CreateDbContextAsync(ct);
        // SQLite's write transaction makes budget checks and reservations atomic across tabs/processes.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var snapshot = await ReadAsync(db, userId, ct);
        if (snapshot.Remaining < credits)
            throw new JournalInputException($"Esta acción necesita {credits} crédito{(credits == 1 ? "" : "s")} y quedan {snapshot.Remaining}. Tu cupo se renueva al terminar el período. Podés seguir escribiendo y usar IA local desde Mi plan.");
        if (kind != "audio" && await db.AiUsageReservations.AnyAsync(row => row.UserId == userId && !row.Completed &&
                row.Kind != "audio" && row.ExpiresUtc > Now, ct))
            throw new JournalInputException("Brisa ya está preparando una respuesta para tu cuenta. Esperá a que termine antes de iniciar otra.");
        var row = new AiUsageReservationRow { Id = Guid.NewGuid(), UserId = userId, Kind = kind, Credits = credits,
            PeriodStartUtc = snapshot.FromUtc, PeriodEndUtc = snapshot.ThroughUtc, CreatedUtc = Now, ExpiresUtc = Now.AddHours(1) };
        db.AiUsageReservations.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(factory, row.Id);
    }
}

public sealed class AiUsageLease(IDbContextFactory<ApplicationDbContext> factory, Guid id) : IAsyncDisposable
{
    private bool completed;
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.AiUsageReservations.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Completed, true), ct);
        completed = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (completed) return;
        await using var db = await factory.CreateDbContextAsync();
        await db.AiUsageReservations.Where(row => row.Id == id && !row.Completed).ExecuteDeleteAsync();
    }
}
