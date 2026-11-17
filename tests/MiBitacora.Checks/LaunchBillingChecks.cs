using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class LaunchBillingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }
    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/launch.db;Pooling=False;Foreign Keys=True;Default Timeout=15"));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddIdentityCore<ApplicationUser>(o => { o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; o.User.RequireUniqueEmail = true; })
            .AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var clock = new Clock { Now = new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero) };
        var environment = new Host(root);
        var options = new SubscriptionOptions { Provider = "MercadoPago", AllowLiveRequests = true, CollectorId = "42",
            AccessToken = "synthetic", WebhookSecret = "synthetic-test-secret", PublicBaseUrl = "https://example.test", MonthlyCredits = 10, ReportCredits = 10 };
        var mail = new TransactionalMail(factory, clock, Options.Create(new MailOptions { Delivery = "Local" }), environment);
        var gateway = new Gateway(clock);
        using var gate = new BillingGate();
        var aiSettings = new JournalAiOptions { OpenAiForAllAccounts = true };
        var billing = new SubscriptionService(factory, gateway, Options.Create(options), environment, gate, clock, Options.Create(aiSettings), mail);
        var usage = new AiUsageService(factory, billing, Options.Create(options), clock, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));
        await using (var db = await factory.CreateDbContextAsync()) { await db.Database.MigrateAsync(); check(!db.Database.HasPendingModelChanges(), "Lanzamiento: modelo y migraciones coinciden"); }
        var owner = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "owner@example.test", Email = "owner@example.test" };
        check((await users.CreateAsync(owner, "Password-Synthetic-2026!")).Succeeded, "Lanzamiento: cuenta de prueba con contraseña real de Identity");
        var requests = new CustomerRequestService(factory, users, mail, Options.Create(options), clock);
        var recovery = new AccountRecoveryService(users, mail, factory, Options.Create(options), clock);
        await recovery.RequestAsync(owner.Email, false);
        string confirmation;
        await using (var db = await factory.CreateDbContextAsync()) {
            var body = (await db.TransactionalEmails.SingleAsync()).Body;
            confirmation = Regex.Match(body, "code=([^\\s]+)").Groups[1].Value;
            check(body.Contains("https://example.test/Account/ConfirmEmail") && !body.Contains("Password-Synthetic"), "Correo: enlace con origen configurado y sin contraseña");
        }
        check(!await recovery.ConfirmAsync("other@example.test", confirmation) && await recovery.ConfirmAsync(owner.Email, confirmation), "Correo: confirmación ligada a la cuenta correcta");
        await recovery.RequestAsync("missing@example.test", true);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.TransactionalEmails.CountAsync() == 1, "Recuperación: una cuenta inexistente no genera correo ni revela datos");
        await recovery.RequestAsync(owner.Email, true);
        string reset;
        await using (var db = await factory.CreateDbContextAsync()) {
            var body = (await db.TransactionalEmails.SingleAsync(row => row.Subject.StartsWith("Recuperá"))).Body;
            reset = Regex.Match(body, "code=([^\\s]+)").Groups[1].Value;
        }
        check((await recovery.ResetAsync(owner.Email, reset, "New-Password-Synthetic-2026!")).Succeeded && !(await recovery.ResetAsync(owner.Email, reset, "Other-Password-Synthetic-2026!")).Succeeded,
            "Recuperación: un enlace permite cambiar la contraseña una sola vez");
        await billing.StartAsync(owner.Id, "payer@example.test");
        var sub = (await billing.GetSummaryAsync(owner.Id))!;
        check(sub.MonthlyPriceCents == 1_500_000 && sub.TermsVersion == BusinessIdentity.TermsVersion && sub.MonthlyCredits == 10 && sub.AcceptedUtc == clock.Now.UtcDateTime,
            "Contratación: guarda ARS 15.000, cupo, fecha y versión aceptada");
        check(!(await billing.GetAccessAsync(owner.Id)).IsPaid, "Contratación: autorizar no alcanza para habilitar Profundo");
        gateway.Payments.Add("p1", new("p1", "42", gateway.Subscription!.ExternalReference, "approved", 1_500_000, "ARS", clock.Now.UtcDateTime, clock.Now.UtcDateTime));
        gateway.Invoices.Add(new("i1", gateway.Subscription.Id, "p1", clock.Now.UtcDateTime));
        await billing.RefreshAsync(owner.Id); await billing.RefreshAsync(owner.Id);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.BillingAudit.CountAsync(row => row.Type == "payment.approved") == 1 && await db.TransactionalEmails.CountAsync(row => row.Key.StartsWith("billing-payment")) == 1,
            "Cobros: la conciliación repetida no duplica constancias ni auditoría");
        options.MonthlyCredits = 200;
        check((await usage.GetAsync(owner.Id)).Limit == 10, "Cupo: una oferta nueva no cambia el cupo ya contratado");
        await using (var failed = await usage.ReserveAsync(owner.Id, "reply", 1)) { }
        check((await usage.GetAsync(owner.Id)).Remaining == 10, "Cupo: un fallo libera la reserva sin descontar créditos");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => Task.Run(async () => {
            try { await using var lease = await usage.ReserveAsync(owner.Id, "audio", 1); await lease.CompleteAsync(); return true; }
            catch (JournalInputException) { return false; }
        })));
        check(concurrent.Count(success => success) == 10 && (await usage.GetAsync(owner.Id)).Remaining == 0, "Cupo: solicitudes concurrentes no superan el saldo mensual");
        try { await usage.ReserveAsync(owner.Id, "report", 10); throw new Exception("Cupo agotado aceptado"); }
        catch (JournalInputException) { check(true, "Cupo: un informe sin saldo se rechaza antes de enviar contenido al proveedor"); }
        await billing.SetAiProviderAsync(owner.Id, "Ollama");
        check((await billing.GetAccessAsync(owner.Id)).AiProvider == "Ollama", "Cupo: se puede elegir IA local al agotar el saldo");
        clock.Now = clock.Now.AddMonths(1);
        gateway.Payments.Add("p2", new("p2", "42", gateway.Subscription.ExternalReference, "approved", 1_500_000, "ARS", clock.Now.UtcDateTime, clock.Now.UtcDateTime));
        gateway.Invoices.Add(new("i2", gateway.Subscription.Id, "p2", clock.Now.UtcDateTime));
        await billing.RefreshAsync(owner.Id);
        check((await usage.GetAsync(owner.Id)).Remaining == 10, "Cupo: la cuota del nuevo período renueva el saldo sin acumular consumo anterior");

        var code = await requests.CreateAsync(owner.Email, "withdraw", "Solicitud sintética", null);
        var receipt = (await requests.GetReceiptAsync(code))!;
        check(receipt.VerifiedUtc is null && receipt.Status == "received", "Arrepentimiento: recibe sin registro y sin ejecutar acciones antes de verificar");
        string token;
        await using (var db = await factory.CreateDbContextAsync()) token = Regex.Match((await db.TransactionalEmails.SingleAsync(row => row.Key == "request-received-" + receipt.Id)).Body, "token=([A-F0-9]+)").Groups[1].Value;
        clock.Now = clock.Now.AddMinutes(5);
        check(!await requests.ConfirmAsync(code, new string('0', 64)) && await requests.ConfirmAsync(code, token) && !await requests.ConfirmAsync(code, token),
            "Arrepentimiento: token aleatorio ligado al pedido y de un solo uso");
        var correctPayment = gateway.Payments["p2"];
        gateway.Payments["p2"] = correctPayment with { CollectorId = "another-merchant" };
        await billing.ProcessCustomerRequestAsync(receipt.Id);
        check(gateway.RefundCalls == 0, "Reintegro: valida vendedor antes de solicitar una devolución de dinero");
        gateway.Payments["p2"] = correctPayment;
        await using (var db = await factory.CreateDbContextAsync()) check((await db.BillingSubscriptions.SingleAsync()).CancellationRequestedUtc == receipt.CreatedUtc,
            "Baja: conserva la fecha original aunque la verificación del correo llegue después");
        gateway.FailRefundAfterAccept = true;
        await billing.ProcessCustomerRequestAsync(receipt.Id);
        check((await requests.GetReceiptAsync(code))?.Status == "processing", "Reintegro: un timeout conserva la solicitud pendiente");
        gateway.FailRefundAfterAccept = false;
        await billing.ProcessCustomerRequestAsync(receipt.Id); await billing.ProcessCustomerRequestAsync(receipt.Id);
        check(gateway.RefundCalls == 1 && (await requests.GetReceiptAsync(code))?.Status == "completed" && !(await billing.GetAccessAsync(owner.Id)).IsPaid,
            "Reintegro: recupera el resultado ambiguo sin duplicar devolución y retira el período devuelto");
        clock.Now = clock.Now.AddMinutes(5);
        gateway.Payments.Add("late", new("late", "42", gateway.Subscription.ExternalReference, "approved", 1_500_000, "ARS", clock.Now.UtcDateTime, clock.Now.UtcDateTime));
        gateway.Invoices.Add(new("late-invoice", gateway.Subscription.Id, "late", clock.Now.UtcDateTime));
        await billing.RefreshAsync(owner.Id);
        Guid lateId;
        await using (var db = await factory.CreateDbContextAsync()) lateId = (await db.CustomerRequests.SingleAsync(row => row.Kind == "refund")).Id;
        await billing.ProcessCustomerRequestAsync(lateId);
        check(gateway.RefundCalls == 2 && gateway.Payments["late"].Status == "refunded", "Baja: un cobro aprobado después del pedido genera reintegro automático");

        environment.EnvironmentName = "Production";
        check(!(await billing.GetAccessAsync(owner.Id)).CanUseOpenAi, "Producción: la configuración histórica OpenAI para todos no habilita una cuenta gratuita");
        check(!new MailOptions { Delivery = "Local" }.IsValid(false), "Producción: no permite simular entrega de correo");
        await users.DeleteAsync((await users.FindByIdAsync(owner.Id))!);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.BillingSubscriptions.AnyAsync() && await db.BillingPayments.CountAsync() == 3 && await db.BillingAudit.AnyAsync() && !await db.AiUsageReservations.AnyAsync(),
            "Borrado: conserva operaciones financieras para conciliar y elimina consumo personal");
        check(await billing.GetSummaryAsync(owner.Id) is null && !(await billing.GetAccessAsync(owner.Id)).CanUseOpenAi, "Borrado: los registros comerciales retenidos no restauran acceso ni una cuenta");
        environment.EnvironmentName = "Development";
        clock.Now = clock.Now.AddMinutes(5);
        gateway.Payments.Add("deleted-late", new("deleted-late", "42", gateway.Subscription.ExternalReference, "approved", 1_500_000, "ARS", clock.Now.UtcDateTime, clock.Now.UtcDateTime));
        gateway.Invoices.Add(new("deleted-invoice", gateway.Subscription.Id, "deleted-late", clock.Now.UtcDateTime));
        await billing.ReconcileAsync(sub.Id);
        await using (var db = await factory.CreateDbContextAsync()) lateId = (await db.CustomerRequests.SingleAsync(row => row.PaymentId == "deleted-late")).Id;
        await billing.ProcessCustomerRequestAsync(lateId);
        check(gateway.Payments["deleted-late"].Status == "refunded", "Borrado: todavía permite devolver una aprobación tardía sin recuperar el diario");
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Host(string root) : IWebHostEnvironment {
        public string EnvironmentName { get; set; } = "Development"; public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root; public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider(); public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Gateway(Clock clock) : ISubscriptionGateway {
        public RemoteSubscription? Subscription;
        public readonly List<RemoteInvoice> Invoices = []; public readonly Dictionary<string, RemotePayment> Payments = [];
        public int RefundCalls; public bool FailRefundAfterAccept;
        public Task<RemoteSubscription> CreateAsync(string reference, string email, long amount, CancellationToken ct) { Subscription = new("remote-1", reference, "42", "authorized", amount, "ARS", 1, "months", clock.Now.UtcDateTime, "https://www.mercadopago.com.ar/subscriptions/checkout?preapproval_id=remote-1"); return Task.FromResult(Subscription); }
        public Task<RemoteSubscription> GetAsync(string id, CancellationToken ct) => Task.FromResult(Subscription!);
        public Task<RemoteSubscription?> FindAsync(string reference, CancellationToken ct) => Task.FromResult(Subscription);
        public Task<RemoteInvoice> GetInvoiceAsync(string id, CancellationToken ct) => Task.FromResult(Invoices.Single(row => row.Id == id));
        public Task<IReadOnlyList<RemoteInvoice>> GetInvoicesAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteInvoice>>(Invoices.ToArray());
        public Task<RemotePayment> GetPaymentAsync(string id, CancellationToken ct) => Task.FromResult(Payments[id]);
        public Task CancelAsync(string id, CancellationToken ct) { Subscription = Subscription! with { Status = "cancelled" }; return Task.CompletedTask; }
        public Task RefundAsync(string paymentId, string key, CancellationToken ct) {
            if (key != "mb-refund-" + paymentId) throw new Exception("Clave de reintegro inestable");
            RefundCalls++; Payments[paymentId] = Payments[paymentId] with { Status = "refunded", RefundedCents = Payments[paymentId].AmountCents, UpdatedUtc = clock.Now.UtcDateTime };
            if (FailRefundAfterAccept) throw new HttpRequestException("Simulated transport timeout after accepted refund");
            return Task.CompletedTask;
        }
    }
}
