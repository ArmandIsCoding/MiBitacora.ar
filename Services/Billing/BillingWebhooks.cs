using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public static class BillingWebhookSignature
{
    public static bool IsResourceId(string value) => value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool Verify(string signature, string requestId, string dataId, string secret, DateTimeOffset now)
    {
        if (!IsResourceId(dataId) || requestId.Length is 0 or > 200 || secret.Length < 16) return false;
        var parts = signature.Split(',').Select(p => p.Split('=', 2)).ToArray();
        if (parts.Count(p => p.Length == 2 && p[0].Trim() == "ts") != 1 ||
            parts.Count(p => p.Length == 2 && p[0].Trim() == "v1") != 1) return false;
        var ts = parts.Single(p => p.Length == 2 && p[0].Trim() == "ts")[1].Trim();
        var hash = parts.Single(p => p.Length == 2 && p[0].Trim() == "v1")[1].Trim();
        if (!long.TryParse(ts, out var timestamp) || timestamp < 0 || hash.Length != 64) return false;
        DateTimeOffset sent;
        try { sent = timestamp > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((now - sent).Duration() > TimeSpan.FromMinutes(10)) return false;
        var manifest = $"id:{dataId.ToLowerInvariant()};request-id:{requestId};ts:{ts};";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hash)); }
        catch (FormatException) { return false; }
    }
}

public sealed class BillingWebhookInbox(IDbContextFactory<ApplicationDbContext> factory, BillingGate gate, TimeProvider clock)
{
    public async Task EnqueueAsync(string type, string resourceId, string requestId, CancellationToken ct)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{type}:{resourceId}:{requestId}")));
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.BillingWebhooks.AnyAsync(e => e.Id == id, ct)) return;
            db.BillingWebhooks.Add(new() { Id = id, Type = type, ResourceId = resourceId,
                CreatedUtc = clock.GetUtcNow().UtcDateTime, NextAttemptUtc = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }
        finally { gate.Semaphore.Release(); }
    }
}

public sealed class BillingWorker(IServiceScopeFactory scopes, IOptions<SubscriptionOptions> options,
    TimeProvider clock, ILogger<BillingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Value.Provider == "MercadoPago" && options.Value.AllowLiveRequests)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                    var service = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                    var gateway = scope.ServiceProvider.GetRequiredService<ISubscriptionGateway>();
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var now = clock.GetUtcNow().UtcDateTime;
                    var events = await db.BillingWebhooks.Where(e => e.ProcessedUtc == null && e.NextAttemptUtc <= now)
                        .OrderBy(e => e.CreatedUtc).Take(25).ToListAsync(stoppingToken);
                    foreach (var notification in events)
                    {
                        try
                        {
                            string? providerId = notification.Type == "subscription_preapproval" ? notification.ResourceId : null;
                            if (notification.Type == "subscription_authorized_payment")
                                providerId = (await gateway.GetInvoiceAsync(notification.ResourceId, stoppingToken)).SubscriptionId;
                            Guid? localId = providerId is null ? null : await db.BillingSubscriptions.Where(s => s.Provider == "MercadoPago" && s.ProviderId == providerId)
                                .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(stoppingToken);
                            if (notification.Type == "payment")
                            {
                                var payment = await gateway.GetPaymentAsync(notification.ResourceId, stoppingToken);
                                localId = await db.BillingSubscriptions.Where(s => s.Provider == "MercadoPago" && s.ExternalReference == payment.ExternalReference)
                                    .Select(s => (Guid?)s.Id).SingleOrDefaultAsync(stoppingToken);
                            }
                            if (localId.HasValue) await service.ReconcileAsync(localId.Value, stoppingToken);
                            notification.ProcessedUtc = now;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            notification.Attempts++;
                            notification.NextAttemptUtc = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(6, notification.Attempts))));
                            logger.LogWarning("Una notificación de suscripción queda pendiente de conciliación. Intento {Attempt}.", notification.Attempts);
                        }
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    var cutoff = now.AddMinutes(-options.Value.ReconcileMinutes);
                    // Include cancelled subscriptions: late approvals, refunds and chargebacks still change paid access.
                    var stale = await db.BillingSubscriptions.AsNoTracking().Where(s => s.Provider == "MercadoPago" && s.Status != "creation_failed" &&
                        (s.LastSyncAttemptUtc == null || s.LastSyncAttemptUtc < cutoff)).OrderBy(s => s.LastSyncAttemptUtc)
                        .Select(s => s.Id).Take(25).ToListAsync(stoppingToken);
                    foreach (var id in stale)
                    {
                        try { await service.ReconcileAsync(id, stoppingToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { logger.LogWarning("No se pudo conciliar una suscripción; se volverá a intentar."); }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("La conciliación de suscripciones se reintentará."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
