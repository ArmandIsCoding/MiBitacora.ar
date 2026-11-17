using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class MailOptions
{
    public string Delivery { get; set; } = "Disabled";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public bool IsValid(bool development) => Delivery == "Disabled" || (Delivery == "Local" && development) ||
        (Delivery == "Smtp" && Host.Length > 0 && Port is > 0 and <= 65535 && EnableSsl &&
            !string.IsNullOrWhiteSpace(Username) && Password.Length > 0 && MailAddress.TryCreate(From, out _));
}

public sealed class TransactionalMail(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock,
    IOptions<MailOptions> options, IWebHostEnvironment environment)
{
    public bool IsReady => options.Value.Delivery != "Disabled" && options.Value.IsValid(environment.IsDevelopment());
    public async Task QueueAsync(string key, string recipient, string subject, string body, CancellationToken ct = default)
    {
        if (!IsReady) throw new JournalInputException("El correo no está disponible. Escribinos a " + BusinessIdentity.SupportEmail + ".");
        if (key.Length > 180 || recipient.Length > 254 || !MailAddress.TryCreate(recipient, out _) || subject.Length > 180 || body.Length > 20_000)
            throw new JournalInputException("No pudimos preparar el mensaje de correo.");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.TransactionalEmails.AnyAsync(row => row.Key == key, ct)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        db.TransactionalEmails.Add(new() { Id = Guid.NewGuid(), Key = key, Recipient = recipient, Subject = subject,
            Body = body, CreatedUtc = now, NextAttemptUtc = now });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            if (!await db.TransactionalEmails.AsNoTracking().AnyAsync(row => row.Key == key, ct)) throw;
        }
    }
}

public sealed class TransactionalMailWorker(IServiceScopeFactory scopes, IOptions<MailOptions> options,
    IWebHostEnvironment environment, TimeProvider clock, ILogger<TransactionalMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = options.Value;
                if (settings.Delivery != "Disabled" && settings.IsValid(environment.IsDevelopment()))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var now = clock.GetUtcNow().UtcDateTime;
                    var pending = await db.TransactionalEmails.Where(row => row.SentUtc == null && row.NextAttemptUtc <= now &&
                            (row.LockedUntilUtc == null || row.LockedUntilUtc <= now))
                        .OrderBy(row => row.CreatedUtc).Take(20).ToListAsync(stoppingToken);
                    foreach (var row in pending)
                    {
                        if (await db.TransactionalEmails.Where(item => item.Id == row.Id && item.SentUtc == null &&
                                (item.LockedUntilUtc == null || item.LockedUntilUtc <= now))
                            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LockedUntilUtc, now.AddMinutes(1)), stoppingToken) == 0) continue;
                        try
                        {
                            if (settings.Delivery == "Local")
                            {
                                var folder = Path.Combine(environment.ContentRootPath, "App_Data", "mail-outbox");
                                Directory.CreateDirectory(folder);
                                await File.WriteAllTextAsync(Path.Combine(folder, row.Id.ToString("N") + ".txt"),
                                    $"To: {row.Recipient}\nSubject: {row.Subject}\n\n{row.Body}", stoppingToken);
                            }
                            else
                            {
                                await SmtpDelivery.SendAsync(settings, row, stoppingToken);
                            }
                            row.SentUtc = now;
                            row.Body = ""; // Do not retain account recovery or identity-verification tokens after delivery.
                        }
                        catch (Exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            row.Attempts++;
                            row.NextAttemptUtc = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(row.Attempts, 6))));
                            logger.LogWarning("Un correo transaccional quedó pendiente. Intento {Attempt}.", row.Attempts);
                        }
                        row.LockedUntilUtc = null;
                        db.Entry(row).Property(item => item.LockedUntilUtc).IsModified = true;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    // Billing keys are retained as delivery tombstones: reconciliation revisits old payments.
                    await db.TransactionalEmails.Where(row => row.SentUtc != null && row.SentUtc < now.AddDays(-30) && !row.Key.StartsWith("billing-"))
                        .ExecuteDeleteAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("El envío de correo se reintentará."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
