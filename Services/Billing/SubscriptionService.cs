using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

// Serialize each account's billing operations without holding up other accounts during network I/O.
public sealed class BillingGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accounts = new(StringComparer.Ordinal);
    public SemaphoreSlim ForAccount(string userId) => _accounts.GetOrAdd(userId, _ => new(1, 1));
    public void Dispose() { Semaphore.Dispose(); foreach (var gate in _accounts.Values) gate.Dispose(); }
}

public sealed class SubscriptionService(IDbContextFactory<ApplicationDbContext> factory, ISubscriptionGateway gateway,
    IOptions<SubscriptionOptions> options, IWebHostEnvironment environment, BillingGate gate, TimeProvider clock,
    IOptions<JournalAiOptions>? aiOptions = null)
{
    private readonly JournalAiOptions aiSettings = aiOptions?.Value ?? new();
    public SubscriptionOptions Settings => options.Value;
    public bool IsSimulation => Settings.Provider == "Simulated" && environment.IsDevelopment();
    public bool CanSubscribe => Settings.Provider != "Disabled" && Settings.IsValid(environment.IsDevelopment());
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private bool ProviderCanGrant(string provider) => provider == "MercadoPago" || (provider == "Simulated" && IsSimulation);
    private static bool Terminal(string state) => state is "cancelled" or "canceled" or "creation_failed";

    public async Task<SubscriptionAccess> GetAccessAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct)) return SubscriptionAccess.Free;
        var grant = await db.BillingAccessGrants.AsNoTracking().SingleOrDefaultAsync(g => g.UserId == userId, ct);
        var preference = await db.AccountAiPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (grant is not null && grant.GrantedUtc <= Now && (grant.ExpiresUtc is null || grant.ExpiresUtc > Now))
            return ApplyAiPolicy(new(true, grant.ExpiresUtc, true), preference);
        var subscriptions = await db.BillingSubscriptions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        var ids = subscriptions.Where(s => ProviderCanGrant(s.Provider)).Select(s => s.Id).ToArray();
        var periods = await db.BillingPayments.AsNoTracking().Where(p => ids.Contains(p.SubscriptionId) && p.Status == "approved")
            .OrderBy(p => p.PeriodStartUtc).ToListAsync(ct);
        return ApplyAiPolicy(Access(periods, Now), preference);
    }

    private SubscriptionAccess ApplyAiPolicy(SubscriptionAccess access, AccountAiPreferenceRow? preference) =>
        access with { AiProvider = preference?.Provider == "Ollama" ? "Ollama" : "OpenAI",
            OpenAiForAllAccounts = aiSettings.OpenAiForAllAccounts };

    public async Task SetAiProviderAsync(string userId, string provider, CancellationToken ct = default)
    {
        if (provider is not ("OpenAI" or "Ollama")) throw new SubscriptionException("Elegí una de las opciones disponibles para conversar con Brisa.");
        if (!(await GetAccessAsync(userId, ct)).CanUseOpenAi) throw new SubscriptionException("Esta elección requiere acceso a OpenAI en tu cuenta.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AccountAiPreferences.SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (row is null) db.AccountAiPreferences.Add(new() { UserId = userId, Provider = provider });
        else row.Provider = provider;
        await db.SaveChangesAsync(ct);
    }

    public static SubscriptionAccess Access(IEnumerable<BillingPaymentRow> payments, DateTime now)
    {
        var through = now;
        foreach (var payment in payments.Where(p => p.Status == "approved").OrderBy(p => p.PeriodStartUtc))
            if (payment.PeriodStartUtc <= through && payment.PeriodEndUtc > through) through = payment.PeriodEndUtc;
        return through > now ? new(true, through) : SubscriptionAccess.Free;
    }

    public async Task<SubscriptionSummary?> GetSummaryAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.BillingSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.CreatedUtc).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var access = await GetAccessAsync(userId, ct);
        return new(row.Id, row.Status, row.CancellationRequested, row.MonthlyPriceCents, access.PaidThroughUtc,
            row.CancellationRequested || Terminal(row.Status) ? null : row.CheckoutUrl, row.Provider);
    }

    public async Task<IReadOnlyList<SubscriptionPayment>> GetPaymentsAsync(string userId, CancellationToken ct = default, int take = 100)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = db.BillingSubscriptions.Where(s => s.UserId == userId).Select(s => s.Id);
        return await db.BillingPayments.AsNoTracking().Where(p => ids.Contains(p.SubscriptionId)).OrderByDescending(p => p.PeriodStartUtc)
            .Select(p => new SubscriptionPayment(p.ProviderPaymentId, p.Status, p.AmountCents, p.PeriodStartUtc, p.PeriodEndUtc)).Take(take).ToListAsync(ct);
    }

    public async Task<string> StartAsync(string userId, string payerEmail, CancellationToken ct = default)
    {
        if (!CanSubscribe) throw new SubscriptionException("Las suscripciones todavía no están habilitadas. Tu plan gratuito sigue disponible.");
        var accountGate = gate.ForAccount(userId);
        await accountGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) throw new SubscriptionException("Volvé a ingresar para contratar el plan.");
            var existing = await db.BillingSubscriptions.Where(s => s.UserId == userId && s.Provider == Settings.Provider)
                .OrderByDescending(s => s.CreatedUtc).ToListAsync(ct);
            var active = existing.FirstOrDefault(s => !Terminal(s.Status));
            if (active is not null)
            {
                if (active.CancellationRequested) throw new SubscriptionException("La cancelación se está confirmando. Esperá antes de volver a contratar.");
                if (active.Status == "pending" && active.CheckoutUrl is not null) return SafeCheckout(active.CheckoutUrl);
                // Ambiguous create timeouts retain the same reference; never create a second recurring charge.
                throw new SubscriptionException("Ya hay una suscripción en curso. Actualizá su estado desde Mi plan; no iniciamos otro cobro.");
            }
            if ((await GetAccessAsync(userId, ct)).IsPaid)
                throw new SubscriptionException("Conservás un período pago. Podés volver a contratar cuando termine, para evitar cobros superpuestos.");
            var row = new BillingSubscriptionRow { Id = Guid.NewGuid(), UserId = userId, Provider = Settings.Provider,
                MonthlyPriceCents = Settings.PriceCents, CreatedUtc = Now };
            row.ExternalReference = "mb_" + row.Id.ToString("N");
            db.BillingSubscriptions.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // A second process (for example during an IIS recycle) may have reserved the account first.
                throw new SubscriptionException("No pudimos reservar la contratación. Actualizá Mi plan antes de volver a intentar.");
            }
            RemoteSubscription remote;
            if (IsSimulation)
                remote = new("sim_" + row.Id.ToString("N"), row.ExternalReference, "simulation", "pending", row.MonthlyPriceCents,
                    "ARS", 1, "months", Now, "/Account/Subscription/Simulate/" + row.Id);
            else
            {
                try { remote = await gateway.CreateAsync(row.ExternalReference, payerEmail, row.MonthlyPriceCents, ct); }
                catch (SubscriptionCreationRejectedException)
                {
                    row.Status = "creation_failed";
                    await db.SaveChangesAsync(CancellationToken.None);
                    throw;
                }
            }
            ValidateSubscription(row, remote);
            row.ProviderId = remote.Id;
            row.Status = remote.Status;
            row.ProviderUpdatedUtc = remote.UpdatedUtc;
            row.CheckoutUrl = remote.CheckoutUrl is null ? null : SafeCheckout(remote.CheckoutUrl);
            row.LastSyncedUtc = Now;
            await db.SaveChangesAsync(ct);
            return row.CheckoutUrl ?? throw new SubscriptionException("Mercado Pago todavía no devolvió el enlace. Actualizá el estado desde Mi plan.");
        }
        finally { accountGate.Release(); }
    }

    public async Task RefreshAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.BillingSubscriptions.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in ids) await ReconcileAsync(id, ct);
    }

    public async Task CancelAsync(string userId, CancellationToken ct = default)
    {
        var accountGate = gate.ForAccount(userId);
        await accountGate.WaitAsync(ct);
        try
        {
            await CancelForDeletionAsync(userId, ct);
        }
        finally { accountGate.Release(); }
    }

    internal SemaphoreSlim AccountGate(string userId) => gate.ForAccount(userId);
    // Caller holds AccountGate through cancellation AND deletion, preventing a new checkout between them.
    internal async Task CancelForDeletionAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.BillingSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        foreach (var row in rows.Where(s => !Terminal(s.Status)))
        {
            row.CancellationRequested = true;
            await db.SaveChangesAsync(ct); // Persist intent, even when the remote request fails.
            if (row.ProviderId is null) throw new SubscriptionException("Hay una contratación pendiente de conciliación. No borramos la cuenta hasta confirmar su cancelación.");
            if (row.Provider == "MercadoPago") await gateway.CancelAsync(row.ProviderId, ct);
            else if (!IsSimulation) throw new SubscriptionException("No se puede gestionar esta suscripción en este entorno.");
            row.Status = "cancelled";
            row.LastSyncedUtc = Now;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ReconcileAsync(Guid id, CancellationToken ct = default)
    {
        await using var lookup = await factory.CreateDbContextAsync(ct);
        var owner = await lookup.BillingSubscriptions.Where(s => s.Id == id).Select(s => s.UserId).SingleOrDefaultAsync(ct);
        if (owner is null) return;
        var accountGate = gate.ForAccount(owner);
        await accountGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.BillingSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
            if (row is null || row.Provider != "MercadoPago" || row.Status == "creation_failed") return;
            row.LastSyncAttemptUtc = Now;
            await db.SaveChangesAsync(ct);
            var remote = row.ProviderId is null
                ? await gateway.FindAsync(row.ExternalReference, ct)
                : await gateway.GetAsync(row.ProviderId, ct);
            if (remote is null) return; // Ambiguous creation remains blocked until it can be reconciled.
            ValidateSubscription(row, remote);
            row.ProviderId ??= remote.Id;
            row.CheckoutUrl ??= remote.CheckoutUrl is null ? null : SafeCheckout(remote.CheckoutUrl);
            if (row.CancellationRequested && !Terminal(remote.Status))
            {
                await gateway.CancelAsync(row.ProviderId, ct);
                remote = await gateway.GetAsync(row.ProviderId, ct);
                ValidateSubscription(row, remote);
            }
            if (row.ProviderUpdatedUtc is null || remote.UpdatedUtc >= row.ProviderUpdatedUtc)
            {
                // A stale active event cannot undo an explicit cancellation.
                if (!Terminal(row.Status) || Terminal(remote.Status)) row.Status = remote.Status;
                row.ProviderUpdatedUtc = remote.UpdatedUtc;
            }
            await db.SaveChangesAsync(ct);
            var invoices = await gateway.GetInvoicesAsync(row.ProviderId, ct);
            if (invoices.Any(i => i.SubscriptionId != row.ProviderId))
                throw new SubscriptionException("La cuota no pertenece a esta suscripción.");
            var refreshed = new HashSet<string>(StringComparer.Ordinal);
            // Recheck old attempts too: an invoice's current payment ID may have changed after an automatic retry.
            var known = await db.BillingPayments.Where(p => p.SubscriptionId == row.Id).OrderBy(p => p.PeriodStartUtc).ToListAsync(ct);
            foreach (var attempt in known)
            {
                var invoice = invoices.FirstOrDefault(i => i.PaymentId == attempt.ProviderPaymentId) ??
                    new RemoteInvoice(attempt.ProviderInvoiceId, row.ProviderId, attempt.ProviderPaymentId, attempt.PeriodStartUtc);
                await ApplyPaymentAsync(db, row, invoice, await gateway.GetPaymentAsync(attempt.ProviderPaymentId, ct), ct);
                await db.SaveChangesAsync(ct);
                refreshed.Add(attempt.ProviderPaymentId);
            }
            foreach (var invoice in invoices.OrderBy(i => i.DebitUtc))
            {
                if (string.IsNullOrEmpty(invoice.PaymentId) || !refreshed.Add(invoice.PaymentId)) continue;
                var payment = await gateway.GetPaymentAsync(invoice.PaymentId, ct);
                await ApplyPaymentAsync(db, row, invoice, payment, ct);
                await db.SaveChangesAsync(ct);
            }
            row.LastSyncedUtc = Now;
            await db.SaveChangesAsync(ct);
        }
        finally { accountGate.Release(); }
    }

    private void ValidateSubscription(BillingSubscriptionRow row, RemoteSubscription remote)
    {
        if ((row.ProviderId is not null && row.ProviderId != remote.Id) || remote.ExternalReference != row.ExternalReference ||
            remote.CollectorId != (row.Provider == "Simulated" ? "simulation" : Settings.CollectorId) ||
            remote.AmountCents != row.MonthlyPriceCents || remote.Currency != "ARS" || remote.Frequency != 1 || remote.FrequencyType != "months" ||
            remote.Status is not ("pending" or "authorized" or "paused" or "cancelled" or "canceled"))
            throw new SubscriptionException("No pudimos validar la suscripción. No cambiamos el acceso de tu cuenta.");
    }

    private async Task ApplyPaymentAsync(ApplicationDbContext db, BillingSubscriptionRow row,
        RemoteInvoice invoice, RemotePayment payment, CancellationToken ct)
    {
        if (payment.Id != invoice.PaymentId || payment.CollectorId != (row.Provider == "Simulated" ? "simulation" : Settings.CollectorId) ||
            payment.ExternalReference != row.ExternalReference || payment.AmountCents != row.MonthlyPriceCents || payment.Currency != row.Currency ||
            payment.Status is not ("approved" or "pending" or "in_process" or "rejected" or "cancelled" or "refunded" or "charged_back"))
            throw new SubscriptionException("No pudimos validar el cobro. Tu acceso ya abonado se conserva.");
        var existing = await db.BillingPayments.SingleOrDefaultAsync(p => p.SubscriptionId == row.Id && p.ProviderPaymentId == payment.Id, ct);
        if (existing is null)
        {
            // An automatic retry may replace the payment ID of the same invoice. Preserve every attempt.
            var invoiceAttempt = await db.BillingPayments.FirstOrDefaultAsync(p => p.SubscriptionId == row.Id && p.ProviderInvoiceId == invoice.Id, ct);
            if (invoiceAttempt is not null && invoiceAttempt.PeriodStartUtc != invoice.DebitUtc)
                throw new SubscriptionException("El período de la cuota cambió; requiere conciliación.");
            existing = new() { Id = Guid.NewGuid(), SubscriptionId = row.Id, ProviderInvoiceId = invoice.Id,
                ProviderPaymentId = payment.Id, AmountCents = payment.AmountCents,
                PeriodStartUtc = invoice.DebitUtc, PeriodEndUtc = NextPeriod(invoice.DebitUtc, row.BillingAnchorUtc ?? invoice.DebitUtc) };
            row.BillingAnchorUtc ??= invoice.DebitUtc;
            db.BillingPayments.Add(existing);
        }
        else if (existing.ProviderInvoiceId != invoice.Id || existing.PeriodStartUtc != invoice.DebitUtc)
            throw new SubscriptionException("El período del cobro cambió; requiere conciliación.");
        if (payment.UpdatedUtc >= existing.ProviderUpdatedUtc)
        {
            // Refunds/chargebacks are terminal: an old approval must never restore their entitlement.
            if (existing.Status is not ("refunded" or "charged_back") || payment.Status is "refunded" or "charged_back")
                existing.Status = payment.Status;
            existing.ProviderUpdatedUtc = payment.UpdatedUtc;
        }
    }

    public async Task SimulateAsync(string userId, Guid id, string action, CancellationToken ct = default)
    {
        if (!IsSimulation) throw new SubscriptionException("La simulación sólo está disponible en desarrollo.");
        var accountGate = gate.ForAccount(userId);
        await accountGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.BillingSubscriptions.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId && s.Provider == "Simulated", ct)
                ?? throw new SubscriptionException("La suscripción no está disponible para tu cuenta.");
            if (Terminal(row.Status) || row.CancellationRequested) throw new SubscriptionException("La suscripción está cancelada.");
            if (action == "authorize") row.Status = "authorized";
            else if (action is "approve" or "renew" or "reject")
            {
                var previous = await db.BillingPayments.Where(p => p.SubscriptionId == id && p.Status == "approved")
                    .OrderByDescending(p => p.PeriodEndUtc).FirstOrDefaultAsync(ct);
                var start = previous is not null && previous.PeriodEndUtc > Now ? previous.PeriodEndUtc : Now;
                var invoiceId = "sim_invoice_" + Guid.NewGuid().ToString("N");
                var paymentId = "sim_payment_" + Guid.NewGuid().ToString("N");
                await ApplyPaymentAsync(db, row, new(invoiceId, row.ProviderId!, paymentId, start),
                    new(paymentId, "simulation", row.ExternalReference, action == "reject" ? "rejected" : "approved", row.MonthlyPriceCents, "ARS", Now), ct);
                row.Status = "authorized";
            }
            else if (action == "refund")
            {
                var payment = await db.BillingPayments.Where(p => p.SubscriptionId == id && p.Status == "approved")
                    .OrderByDescending(p => p.PeriodStartUtc).FirstOrDefaultAsync(ct) ?? throw new SubscriptionException("No hay un pago aprobado para devolver.");
                payment.Status = "refunded";
                payment.ProviderUpdatedUtc = Now;
            }
            else throw new SubscriptionException("La acción de prueba no es válida.");
            await db.SaveChangesAsync(ct);
        }
        finally { accountGate.Release(); }
    }

    private string SafeCheckout(string url)
    {
        if (IsSimulation && url.StartsWith("/Account/Subscription/Simulate/", StringComparison.Ordinal)) return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "www.mercadopago.com.ar" &&
            uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.AbsolutePath == "/subscriptions/checkout") return url;
        throw new SubscriptionException("El enlace de contratación no es válido.");
    }

    public static DateTime NextPeriod(DateTime start, DateTime anchor)
    {
        var month = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        return new DateTime(month.Year, month.Month, Math.Min(anchor.Day, DateTime.DaysInMonth(month.Year, month.Month)),
            start.Hour, start.Minute, start.Second, DateTimeKind.Utc).AddTicks(start.Ticks % TimeSpan.TicksPerSecond);
    }
}
