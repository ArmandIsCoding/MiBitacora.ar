using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed partial class SubscriptionService
{
    public async Task<string> RequestCancellationAsync(string userId, string email, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.CustomerRequests.Where(row => row.UserId == userId && row.Kind == "cancel" && row.Status == "processing" && row.VerifiedUtc != null)
            .OrderByDescending(row => row.CreatedUtc).FirstOrDefaultAsync(ct);
        var request = existing ?? new CustomerRequestRow { Id = Guid.NewGuid(),
            Code = "MB-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)),
            UserId = userId, Email = email, Kind = "cancel", Status = "processing", CreatedUtc = Now, VerifiedUtc = Now };
        if (existing is null) { db.CustomerRequests.Add(request); await db.SaveChangesAsync(ct); }
        await ProcessCustomerRequestAsync(request.Id, ct);
        return request.Code;
    }

    private async Task AuditAsync(ApplicationDbContext db, string type, string reference, string userId,
        Guid? subscriptionId = null, long amountCents = 0, CancellationToken ct = default)
    {
        if (await db.BillingAudit.AnyAsync(row => row.Type == type && row.Reference == reference, ct)) return;
        db.BillingAudit.Add(new() { Id = Guid.NewGuid(), Type = type, Reference = reference, UserId = userId,
            SubscriptionId = subscriptionId, AmountCents = amountCents, CreatedUtc = Now });
    }

    private async Task NotifyPaymentAsync(BillingSubscriptionRow subscription, BillingPaymentRow payment, CancellationToken ct)
    {
        if (mail?.IsReady != true || subscription.PayerEmail.Length == 0) return;
        var status = payment.Status switch { "approved" => "aprobado", "refunded" => "devuelto", "charged_back" => "con contracargo", "rejected" => "rechazado", _ => "pendiente" };
        var price = (payment.AmountCents / 100m).ToString("C0", System.Globalization.CultureInfo.GetCultureInfo("es-AR"));
        await mail.QueueAsync($"billing-payment-{payment.ProviderPaymentId}-{payment.Status}", subscription.PayerEmail,
            $"Mi Bitácora · pago {status}",
            $"El pago de tu Plan Profundo está {status}.\nImporte: ARS {price}.\nPeríodo: {payment.PeriodStartUtc:yyyy-MM-dd} a {payment.PeriodEndUtc:yyyy-MM-dd} (UTC).\nReferencia de Mercado Pago: {payment.ProviderPaymentId}.\n\nPodés revisar la vigencia y el historial desde Mi cuenta → Mi plan. Una cuota rechazada no agrega un nuevo período; tu cuaderno y los períodos ya abonados se conservan.\n\nEsta es una constancia de operación, no una factura fiscal. Para tu factura C, contactanos indicando esta referencia.\n{BusinessIdentity.Name}\nCUIT {BusinessIdentity.TaxId}\n{BusinessIdentity.Address}\n{BusinessIdentity.SupportEmail}", ct);
    }

    public async Task ProcessCustomerRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var lookup = await factory.CreateDbContextAsync(ct);
        var owner = await lookup.CustomerRequests.Where(row => row.Id == requestId).Select(row => row.UserId).SingleOrDefaultAsync(ct);
        if (owner is null)
        {
            await lookup.CustomerRequests.Where(row => row.Id == requestId && row.VerifiedUtc != null && row.Status != "completed")
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, "needs_review"), ct);
            return;
        }
        var accountGate = gate.ForAccount(owner);
        await accountGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var request = await db.CustomerRequests.SingleOrDefaultAsync(row => row.Id == requestId, ct);
            if (request is null || request.VerifiedUtc is null || request.Status is "completed" or "received" || request.Kind == "support") return;
            request.Attempts++; request.LastAttemptUtc = Now;
            await db.SaveChangesAsync(ct);
            try
            {
                // Identity verification may arrive later; the effective request date stays the original one.
                await db.BillingSubscriptions.Where(row => row.UserId == owner &&
                    (row.CancellationRequestedUtc == null || row.CancellationRequestedUtc > request.CreatedUtc))
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.CancellationRequestedUtc, request.CreatedUtc), ct);
                await CancelForDeletionAsync(owner, ct); // Same durable cancellation and account lock as normal cancellation.
                var subscriptionIds = await db.BillingSubscriptions.Where(row => row.UserId == owner).Select(row => row.Id).ToListAsync(ct);
                var payments = await db.BillingPayments.Where(row => subscriptionIds.Contains(row.SubscriptionId) && row.Status == "approved")
                    .OrderByDescending(row => row.ApprovedUtc ?? row.PeriodStartUtc).ToListAsync(ct);
                if (request.Kind is "withdraw" or "refund")
                {
                    var payment = request.PaymentId is { Length: > 0 } paymentId
                        ? payments.SingleOrDefault(row => row.ProviderPaymentId == paymentId) : payments.FirstOrDefault();
                    if (payment is not null)
                    {
                        request.PaymentId = payment.ProviderPaymentId; request.SubscriptionId = payment.SubscriptionId;
                        var paidAt = payment.ApprovedUtc ?? payment.PeriodStartUtc;
                        var requestedDay = JournalDates.ToLocalDate(request.CreatedUtc, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));
                        var paidDay = JournalDates.ToLocalDate(paidAt, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));
                        if (!request.RefundApprovedByAdmin && (requestedDay < paidDay || requestedDay > paidDay.AddDays(Settings.WithdrawalDays)))
                        {
                            request.Status = "needs_review";
                            request.Resolution = "Renovación cancelada. El reintegro requiere revisión de atención al cliente por el plazo de la solicitud.";
                            await db.SaveChangesAsync(ct);
                            return;
                        }
                        var subscription = await db.BillingSubscriptions.SingleAsync(row => row.Id == payment.SubscriptionId, ct);
                        if (subscription.Provider == "MercadoPago")
                        {
                            var remote = await gateway.GetPaymentAsync(payment.ProviderPaymentId, ct);
                            // Validate ownership, seller, currency and amount BEFORE any money movement.
                            await ApplyPaymentAsync(db, subscription, new(payment.ProviderInvoiceId, subscription.ProviderId!, payment.ProviderPaymentId, payment.PeriodStartUtc), remote, ct);
                            if ((remote.RefundedCents > 0 && remote.RefundedCents < remote.AmountCents) || remote.Status is not ("approved" or "refunded")) {
                                request.Status = "needs_review";
                                request.Resolution = "Renovación cancelada. El pago tiene una devolución parcial u otro estado que requiere revisión en Mercado Pago.";
                                await db.SaveChangesAsync(ct);
                                return;
                            }
                            if (remote.Status != "refunded" && remote.RefundedCents < remote.AmountCents)
                                await gateway.RefundAsync(payment.ProviderPaymentId, "mb-refund-" + payment.ProviderPaymentId, ct);
                            remote = await gateway.GetPaymentAsync(payment.ProviderPaymentId, ct);
                            await ApplyPaymentAsync(db, subscription, new(payment.ProviderInvoiceId, subscription.ProviderId!, payment.ProviderPaymentId, payment.PeriodStartUtc), remote, ct);
                            if (payment.Status != "refunded") throw new SubscriptionException("El reintegro sigue pendiente de confirmación.");
                        }
                        else if (IsSimulation)
                        { payment.Status = "refunded"; payment.RefundedCents = payment.AmountCents; payment.ProviderUpdatedUtc = Now; }
                        else throw new SubscriptionException("El reintegro requiere revisión del proveedor.");
                        await AuditAsync(db, "refund.approved", payment.ProviderPaymentId, owner, payment.SubscriptionId, payment.AmountCents, ct);
                    }
                }
                request.Status = "completed"; request.ResolvedUtc = Now;
                request.Resolution = request.Kind == "cancel" ? "Renovación cancelada. Se conserva el período abonado." : "Renovación cancelada. Los pagos identificados como reintegrables fueron devueltos; si había un cobro en tránsito, se conciliará también.";
                await AuditAsync(db, "request.completed", request.Code, owner, request.SubscriptionId, ct: ct);
                if (mail?.IsReady == true)
                    await mail.QueueAsync("request-resolved-" + request.Id, request.Email, "Tu solicitud " + request.Code + " está resuelta",
                        $"Código: {request.Code}\n{request.Resolution}\n\nLa acreditación de un reintegro depende de Mercado Pago y del medio de pago. No borra tu cuaderno.\n\n{BusinessIdentity.Name}\n{BusinessIdentity.SupportEmail}", ct);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is SubscriptionException or HttpRequestException or TaskCanceledException)
            {
                request.Status = "processing";
                await db.SaveChangesAsync(CancellationToken.None);
                // Keep the durable request for the worker; no second refund intent is created.
            }
        }
        finally { accountGate.Release(); }
    }
}
