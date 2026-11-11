using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class BillingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-billing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var clock = new Clock(new DateTimeOffset(2027, 1, 31, 12, 0, 0, TimeSpan.Zero));
        var config = new SubscriptionOptions { Provider = "MercadoPago", AllowLiveRequests = true,
            AccessToken = "synthetic-test-token", CollectorId = "42", WebhookSecret = "synthetic-webhook-secret",
            PublicBaseUrl = "https://example.test", MonthlyPriceArs = 8000 };
        var environment = new Environment();
        using var gate = new BillingGate();
        var gateway = new Gateway(clock);
        var billing = new SubscriptionService(factory, gateway, Options.Create(config), environment, gate, clock);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            check(!db.Database.HasPendingModelChanges(), "Suscripciones: la migración coincide con el modelo de SQLite");
            db.Users.AddRange(new ApplicationUser { Id = "a", UserName = "a" }, new ApplicationUser { Id = "b", UserName = "b" },
                new ApplicationUser { Id = "timeout", UserName = "timeout" }, new ApplicationUser { Id = "simulation", UserName = "simulation" });
            await db.SaveChangesAsync();
        }
        check(!(await billing.GetAccessAsync("a")).IsPaid, "Suscripciones: una cuenta nueva usa el plan gratuito");
        var checkout = await billing.StartAsync("a", "payer@example.test");
        check(checkout.StartsWith("https://www.mercadopago.com.ar/subscriptions/checkout?"), "Suscripciones: checkout individual vinculado a una referencia opaca");
        await billing.StartAsync("a", "other@example.test");
        check(gateway.Creates == 1, "Suscripciones: doble clic no crea dos cobros recurrentes");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.BillingSubscriptions.Add(new BillingSubscriptionRow { Id = Guid.NewGuid(), UserId = "a", Provider = "MercadoPago",
                ExternalReference = "duplicate-process-intent", MonthlyPriceCents = 800000, CreatedUtc = clock.GetUtcNow().UtcDateTime });
            try { await db.SaveChangesAsync(); throw new Exception("Se aceptaron dos intenciones activas para la misma cuenta"); }
            catch (DbUpdateException) { check(true, "Suscripciones: SQLite impide otra contratación activa incluso desde un segundo proceso"); }
        }
        var subscription = (await billing.GetSummaryAsync("a"))!;
        var remoteId = gateway.LastId;
        gateway.Subscriptions[remoteId] = gateway.Subscriptions[remoteId] with { Status = "authorized" };
        await billing.RefreshAsync("a");
        check(!(await billing.GetAccessAsync("a")).IsPaid, "Suscripciones: autorizar sin pagar no desbloquea OpenAI");
        gateway.AddPayment(remoteId, "payment1", clock.GetUtcNow().UtcDateTime);
        await billing.RefreshAsync("a");
        var first = await billing.GetAccessAsync("a");
        check(first.IsPaid && first.PaidThroughUtc == new DateTime(2027, 2, 28, 12, 0, 0, DateTimeKind.Utc),
            "Suscripciones: un pago aprobado acredita el período de la cuota");
        await billing.RefreshAsync("a");
        check((await billing.GetAccessAsync("a")).PaidThroughUtc == first.PaidThroughUtc && (await billing.GetPaymentsAsync("a")).Count == 1,
            "Suscripciones: notificaciones repetidas no duplican pagos ni meses");
        check(!(await billing.GetAccessAsync("b")).IsPaid && (await billing.GetPaymentsAsync("b")).Count == 0,
            "Suscripciones: los cobros no habilitan otra cuenta");

        gateway.AddPayment(remoteId, "payment2", first.PaidThroughUtc!.Value);
        await billing.RefreshAsync("a");
        check((await billing.GetAccessAsync("a")).PaidThroughUtc == new DateTime(2027, 3, 31, 12, 0, 0, DateTimeKind.Utc),
            "Suscripciones: la renovación conserva el ancla del día 31 después de febrero");
        gateway.Payments["payment2"] = gateway.Payments["payment2"] with { Status = "rejected", UpdatedUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(1) };
        await billing.RefreshAsync("a");
        check((await billing.GetAccessAsync("a")).PaidThroughUtc == first.PaidThroughUtc,
            "Suscripciones: un rechazo conserva sólo el período previamente pagado");
        await billing.CancelAsync("a");
        check(gateway.Cancels == 1 && (await billing.GetAccessAsync("a")).IsPaid,
            "Suscripciones: cancelar detiene renovaciones y conserva el período abonado");

        gateway.Payments["payment1"] = gateway.Payments["payment1"] with { Status = "refunded", UpdatedUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(2) };
        await billing.RefreshAsync("a");
        check(!(await billing.GetAccessAsync("a")).IsPaid, "Suscripciones: un reembolso retira el acceso de ese período");
        gateway.Payments["payment1"] = gateway.Payments["payment1"] with { Status = "approved", UpdatedUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-1) };
        await billing.RefreshAsync("a");
        check(!(await billing.GetAccessAsync("a")).IsPaid, "Suscripciones: un evento anterior no restaura un cobro devuelto");

        gateway.FailCreateAfterAccept = true;
        try { await billing.StartAsync("timeout", "payer@example.test"); throw new Exception("Se esperaba timeout"); }
        catch (HttpRequestException) { }
        gateway.FailCreateAfterAccept = false;
        await RejectAsync(() => billing.StartAsync("timeout", "payer@example.test"), "Suscripciones: timeout ambiguo impide duplicar la contratación");
        await billing.RefreshAsync("timeout");
        check((await billing.GetSummaryAsync("timeout"))?.CheckoutUrl is not null,
            "Suscripciones: la conciliación recupera una contratación aceptada antes del timeout");
        var timeoutId = gateway.LastId;
        var correct = gateway.Subscriptions[timeoutId];
        gateway.Subscriptions[timeoutId] = correct with { CollectorId = "another-merchant" };
        await RejectAsync(() => billing.RefreshAsync("timeout"), "Suscripciones: se rechaza un vendedor diferente");
        gateway.Subscriptions[timeoutId] = correct with { ExternalReference = "another-user-reference" };
        await RejectAsync(() => billing.RefreshAsync("timeout"), "Suscripciones: se rechaza una referencia de otra cuenta");
        gateway.Subscriptions[timeoutId] = correct;
        gateway.AddPayment(timeoutId, "wrong-money", clock.GetUtcNow().UtcDateTime);
        gateway.Payments["wrong-money"] = gateway.Payments["wrong-money"] with { AmountCents = 1 };
        await RejectAsync(() => billing.RefreshAsync("timeout"), "Suscripciones: un importe manipulado no habilita el plan");

        config.Provider = "Simulated"; config.AllowLiveRequests = false; config.PublicBaseUrl = "http://localhost:5264";
        var simulated = await billing.StartAsync("simulation", "simulation@example.test");
        check(simulated.StartsWith("/Account/Subscription/Simulate/") && gateway.Creates == 2,
            "Suscripciones: el simulador no llama al gateway de Mercado Pago");
        var sim = (await billing.GetSummaryAsync("simulation"))!;
        await RejectAsync(() => billing.SimulateAsync("b", sim.Id, "approve"), "Suscripciones: una cuenta no puede aprobar la simulación de otra");
        await billing.SimulateAsync("simulation", sim.Id, "authorize");
        check(!(await billing.GetAccessAsync("simulation")).IsPaid, "Simulación: autorización sola conserva IA local");
        await billing.SimulateAsync("simulation", sim.Id, "approve");
        check((await billing.GetAccessAsync("simulation")).IsPaid, "Simulación: aprobar una cuota habilita el plan localmente");
        environment.EnvironmentName = "Production";
        check(!config.IsValid(false) && !(await billing.GetAccessAsync("simulation")).IsPaid,
            "Suscripciones: un pago simulado nunca habilita OpenAI en producción");
        environment.EnvironmentName = "Development";

        var ts = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var manifest = $"id:123;request-id:request-1;ts:{ts};";
        var signature = "ts=" + ts + ",v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.WebhookSecret), Encoding.UTF8.GetBytes(manifest)));
        check(BillingWebhookSignature.Verify(signature, "request-1", "123", config.WebhookSecret, clock.GetUtcNow()), "Webhooks: firma HMAC válida");
        check(!BillingWebhookSignature.Verify(signature, "request-1", "124", config.WebhookSecret, clock.GetUtcNow()) &&
            !BillingWebhookSignature.Verify(signature, "request-2", "123", config.WebhookSecret, clock.GetUtcNow()), "Webhooks: firma ligada al recurso y al request");
        check(!BillingWebhookSignature.Verify(signature, "request-1", "123", config.WebhookSecret, clock.GetUtcNow().AddHours(1)), "Webhooks: se rechaza replay fuera de ventana");
        var inbox = new BillingWebhookInbox(factory, gate, clock);
        await inbox.EnqueueAsync("payment", "123", "request-1", default);
        await inbox.EnqueueAsync("payment", "123", "request-1", default);
        await using (var db = await factory.CreateDbContextAsync()) check(await db.BillingWebhooks.CountAsync() == 1, "Webhooks: bandeja durable sin duplicar una misma entrega");

        await CheckRoutingAsync(billing, clock, factory, check);
        await CheckRecoveryAsync(billing, gateway, factory, provider, root, clock, check);
        await CheckGatewayAsync(environment, check);

        async Task RejectAsync(Func<Task> action, string label)
        {
            try { await action(); throw new Exception(label + ": la operación fue aceptada"); }
            catch (SubscriptionException) { check(true, label); }
        }
    }

    private static async Task CheckRecoveryAsync(SubscriptionService billing, Gateway gateway,
        IDbContextFactory<ApplicationDbContext> factory, IServiceProvider provider, string root, Clock clock, Action<bool, string> check)
    {
        billing.Settings.Provider = "MercadoPago"; billing.Settings.AllowLiveRequests = true;
        billing.Settings.PublicBaseUrl = "https://example.test";
        var deleteId = Guid.NewGuid().ToString();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.AddRange(new ApplicationUser { Id = "retry", UserName = "retry" },
                new ApplicationUser { Id = "definite-reject", UserName = "definite-reject" },
                new ApplicationUser { Id = deleteId, UserName = "delete" });
            await db.SaveChangesAsync();
        }
        await billing.StartAsync("retry", "retry@example.test");
        var id = gateway.LastId;
        gateway.AddPayment(id, "retry-rejected", clock.GetUtcNow().UtcDateTime);
        gateway.Payments["retry-rejected"] = gateway.Payments["retry-rejected"] with { Status = "rejected" };
        await billing.RefreshAsync("retry");
        var invoice = gateway.Invoices.Single(i => i.PaymentId == "retry-rejected");
        gateway.Invoices.Remove(invoice);
        gateway.Invoices.Add(invoice with { PaymentId = "retry-approved" });
        gateway.Payments["retry-approved"] = gateway.Payments["retry-rejected"] with { Id = "retry-approved", Status = "approved" };
        await billing.RefreshAsync("retry");
        check((await billing.GetAccessAsync("retry")).IsPaid && (await billing.GetPaymentsAsync("retry")).Count == 2,
            "Suscripciones: un reintento aprobado de la misma cuota acredita acceso y conserva el intento rechazado");
        gateway.Payments["retry-approved"] = gateway.Payments["retry-approved"] with { Status = "refunded", UpdatedUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(1) };
        gateway.Invoices.Clear(); // A late refund must still be checked if the search stops returning that attempt.
        await billing.RefreshAsync("retry");
        check(!(await billing.GetAccessAsync("retry")).IsPaid,
            "Suscripciones: concilia el reembolso de un intento que ya no aparece en la búsqueda de cuotas");

        gateway.RejectCreate = true;
        try { await billing.StartAsync("definite-reject", "reject@example.test"); throw new Exception("Se esperaba un rechazo definitivo"); }
        catch (SubscriptionCreationRejectedException) { }
        check((await billing.GetSummaryAsync("definite-reject"))?.Status == "creation_failed",
            "Suscripciones: un rechazo definitivo de contratación no deja una intención ambigua");
        gateway.RejectCreate = false;
        check((await billing.StartAsync("definite-reject", "reject@example.test")).StartsWith("https://"),
            "Suscripciones: permite reintentar una contratación rechazada de forma definitiva");

        await billing.StartAsync(deleteId, "delete@example.test");
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(deleteId))!;
        var deletion = new AccountDeletionService(users, new UploadFileStore(Path.Combine(root, "uploads"), NullLogger<UploadFileStore>.Instance), billing);
        gateway.FailCancel = true;
        var failure = await deletion.DeleteAsync(user);
        await using (var db = await factory.CreateDbContextAsync())
            check(!failure.Succeeded && await db.Users.AnyAsync(u => u.Id == deleteId) &&
                (await billing.GetSummaryAsync(deleteId))?.CancellationRequested == true,
                "Borrado de cuenta: conserva los datos si no puede confirmar la cancelación y persiste el pedido");
        gateway.FailCancel = false;
        check((await deletion.DeleteAsync(user)).Succeeded && await billing.GetSummaryAsync(deleteId) is null,
            "Borrado de cuenta: cancela la renovación antes de eliminar la cuenta y sus datos de suscripción");
    }

    private static async Task CheckRoutingAsync(SubscriptionService billing, Clock clock,
        IDbContextFactory<ApplicationDbContext> factory, Action<bool, string> check)
    {
        using var localGate = new LocalInferenceGate(); using var cloudGate = new LocalInferenceGate();
        var localCalls = 0; var cloudCalls = 0;
        using var localHttp = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref localCalls);
            if (request.RequestUri!.AbsolutePath == "/v1/audio/transcriptions")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { text = "audio local" }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { done = true, done_reason = "stop", message = new { content = "local" } }) });
        }));
        using var cloudHttp = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref cloudCalls);
            if (request.RequestUri!.AbsolutePath == "/v1/audio/transcriptions")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { text = "audio cloud" }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "cloud" } } } } }) });
        }));
        var local = new OllamaModelClient(localHttp, Options.Create(new OllamaOptions()), localGate, NullLogger.Instance);
        var cloud = new OpenAiModelClient(cloudHttp, Options.Create(new OpenAiOptions { ApiKey = "synthetic", ProjectId = "proj_test" }), cloudGate, NullLogger.Instance);
        var router = new AccountAiRouter(billing, local, cloud);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } });
        var replies = await Task.WhenAll(Run("b"), Run("simulation"));
        check(replies[0] == "local" && replies[1] == "cloud" && localCalls == 1 && cloudCalls == 1,
            "IA por plan: operaciones concurrentes de cuentas gratis y pagas no mezclan proveedores");
        try { await router.GenerateAsync("instruction", "prompt", schema, [], default); throw new Exception("Faltó contexto de cuenta"); }
        catch (JournalReflectionException) { check(cloudCalls == 1 && localCalls == 1, "IA por plan: una llamada sin cuenta no envía datos a ningún proveedor"); }
        var audioRouter = new AccountAudioTranscriber(router,
            new OpenAiAudioTranscriber(cloudHttp, Options.Create(new OpenAiOptions { ApiKey = "synthetic", ProjectId = "proj_test" }),
                Options.Create(new JournalAiOptions { Provider = "OpenAI" }), Options.Create(new AudioTranscriptionOptions()), cloudGate, NullLogger<OpenAiAudioTranscriber>.Instance),
            new OllamaAudioTranscriber(localHttp, Options.Create(new OllamaOptions()), localGate));
        var audioReplies = await Task.WhenAll(Audio("b"), Audio("simulation"));
        check(audioReplies[0] == "audio local" && audioReplies[1] == "audio cloud" && localCalls == 2 && cloudCalls == 2,
            "IA por plan: la transcripción de una cuenta gratuita permanece local y la de una paga usa OpenAI");
        using (router.Use(await router.ResolveAsync("b")))
        {
            try
            {
                using var stream = new MemoryStream(new byte[44]);
                await audioRouter.TranscribeAsync(new("clip.mp3", "audio/mpeg", stream));
                throw new Exception("El formato local no compatible debe rechazarse");
            }
            catch (JournalReflectionException) { check(cloudCalls == 2, "Audio local: un formato no compatible no se deriva a OpenAI"); }
        }
        var sim = (await billing.GetSummaryAsync("simulation"))!;
        using (router.Use(await router.ResolveAsync("simulation")))
        {
            await billing.SimulateAsync("simulation", sim.Id, "refund");
            try { await router.GenerateAsync("instruction", "prompt", schema, [], default); throw new Exception("Se usó OpenAI después de un reembolso"); }
            catch (JournalReflectionException) { check(cloudCalls == 2, "IA por plan: revalida el acceso antes de cada llamada a OpenAI"); }
        }
        clock.Now = clock.Now.AddMonths(3);
        check(!(await billing.GetAccessAsync("a")).IsPaid, "IA por plan: un período vencido vuelve al acceso gratuito");
        async Task<string> Run(string id)
        {
            using var operation = router.Use(await router.ResolveAsync(id));
            await Task.Yield();
            return await router.GenerateAsync("instruction", "prompt", schema, [], default);
        }
        async Task<string> Audio(string id)
        {
            using var operation = router.Use(await router.ResolveAsync(id));
            using var stream = new MemoryStream(new byte[44]);
            return await audioRouter.TranscribeAsync(new("clip.wav", "audio/wav", stream));
        }
    }

    private static async Task CheckGatewayAsync(Environment environment, Action<bool, string> check)
    {
        var config = new SubscriptionOptions(); var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new Exception("La red debe quedar deshabilitada"); }));
        var gateway = new MercadoPagoGateway(http, Options.Create(config), environment);
        try { await gateway.CreateAsync("ref", "test@example.test", 100, default); throw new Exception("Se habilitó la red sin configurar"); }
        catch (SubscriptionException) { check(calls == 0, "Mercado Pago: valores predeterminados nunca hacen solicitudes reales"); }
        config.Provider = "MercadoPago"; config.AllowLiveRequests = true; config.AccessToken = "synthetic-test-token";
        config.WebhookSecret = "synthetic-webhook-secret"; config.CollectorId = "42";
        config.PublicBaseUrl = "https://example.test"; config.MonthlyPriceArs = 8000;
        var offsets = new List<int>(); var postValidated = false; var cancelValidated = false; var cancelLegacy = false;
        var cancelRequests = new List<string>(); var searchPages = 0;
        var errorStatus = HttpStatusCode.OK;
        using var fixtureHttp = new HttpClient(new Handler(async (request, ct) =>
        {
            check(request.RequestUri!.Host == "api.mercadopago.com" && request.RequestUri.Scheme == "https" &&
                request.Headers.Authorization?.ToString() == "Bearer synthetic-test-token", "Mercado Pago: host fijo HTTPS y credencial sólo en el servidor");
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var body = json.RootElement;
                postValidated = body.GetProperty("external_reference").GetString() == "opaque-ref" &&
                    body.GetProperty("status").GetString() == "pending" && body.GetProperty("payer_email").GetString() == "payer@example.test" &&
                    body.GetProperty("auto_recurring").GetProperty("transaction_amount").GetDecimal() == 8000 &&
                    body.GetProperty("back_url").GetString() == "https://example.test/Account/Manage/Subscription?returned=1" &&
                    request.Headers.GetValues("X-Idempotency-Key").Single() == "opaque-ref";
                if (errorStatus != HttpStatusCode.OK) return new HttpResponseMessage(errorStatus);
            }
            if (request.Method == HttpMethod.Put)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var status = json.RootElement.GetProperty("status").GetString()!;
                cancelRequests.Add(status);
                if (cancelLegacy && status == "canceled") return new HttpResponseMessage(HttpStatusCode.BadRequest);
                cancelValidated = status is "canceled" or "cancelled";
                return Json(new { id = "sub-test", status });
            }
            if (request.RequestUri.AbsolutePath == "/authorized_payments/search")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var offset = int.Parse(query["offset"].ToString()); offsets.Add(offset);
                return Json(new { paging = new { total = 2, limit = 1, offset },
                    results = new[] { new { id = "invoice-" + offset, preapproval_id = "sub-test", payment = new { id = 123 + offset }, debit_date = "2027-01-31T12:00:00Z" } } });
            }
            if (request.RequestUri.AbsolutePath == "/v1/payments/123")
                return Json(new { id = 123, collector_id = 42, external_reference = "opaque-ref", status = "approved", transaction_amount = 8000m, currency_id = "ARS", date_last_updated = "2027-01-31T12:00:00Z" });
            if (request.RequestUri.AbsolutePath == "/preapproval/search")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var offset = int.Parse(query["offset"].ToString()); searchPages++;
                return Json(new { paging = new { total = 2, limit = 1, offset }, results = new[] { new {
                    id = offset == 0 ? "other-sub" : "sub-test", collector_id = 42, external_reference = offset == 0 ? "another-reference" : "opaque-ref", status = "pending",
                    auto_recurring = new { transaction_amount = 8000m, currency_id = "ARS", frequency = 1, frequency_type = "months" },
                    last_modified = "2027-01-31T12:00:00Z", init_point = "https://www.mercadopago.com.ar/subscriptions/checkout?preapproval_id=sub-test" } } });
            }
            return Json(new { id = "sub-test", collector_id = 42, external_reference = "opaque-ref", status = "pending",
                auto_recurring = new { transaction_amount = 8000m, currency_id = "ARS", frequency = 1, frequency_type = "months" },
                last_modified = "2027-01-31T12:00:00Z", init_point = "https://www.mercadopago.com.ar/subscriptions/checkout?preapproval_id=sub-test" });
        }));
        var fixture = new MercadoPagoGateway(fixtureHttp, Options.Create(config), environment);
        var created = await fixture.CreateAsync("opaque-ref", "payer@example.test", 800000, default);
        check(postValidated && created.Id == "sub-test" && created.AmountCents == 800000 && created.CollectorId == "42",
            "Mercado Pago: contrato JSON mensual, referencia e idempotencia sin datos del diario");
        var invoices = await fixture.GetInvoicesAsync(created.Id, default);
        check(offsets.SequenceEqual(new[] { 0, 1 }) && invoices.Count == 2 && invoices[1].PaymentId == "124",
            "Mercado Pago: recorre todas las páginas aunque la API reduzca el límite solicitado");
        var paid = await fixture.GetPaymentAsync("123", default);
        check(paid.Id == "123" && paid.CollectorId == "42" && paid.AmountCents == 800000, "Mercado Pago: interpreta identificadores numéricos e importes exactos");
        check((await fixture.FindAsync("opaque-ref", default))?.Id == "sub-test" && searchPages == 2,
            "Mercado Pago: recupera una contratación por su referencia exacta en otra página");
        await fixture.CancelAsync(created.Id, default);
        check(cancelValidated, "Mercado Pago: cancela futuras renovaciones mediante la API de suscripciones");
        cancelLegacy = true; cancelRequests.Clear();
        await fixture.CancelAsync(created.Id, default);
        check(cancelRequests.SequenceEqual(new[] { "canceled", "cancelled" }),
            "Mercado Pago: acepta la variante de cancelación de la API anterior sólo tras un rechazo definitivo");
        errorStatus = HttpStatusCode.BadRequest;
        try { await fixture.CreateAsync("opaque-ref", "payer@example.test", 800000, default); throw new Exception("Se esperaba rechazo definitivo"); }
        catch (SubscriptionCreationRejectedException) { check(true, "Mercado Pago: un 400 al crear es un rechazo definitivo"); }
        errorStatus = HttpStatusCode.InternalServerError;
        try { await fixture.CreateAsync("opaque-ref", "payer@example.test", 800000, default); throw new Exception("Se esperaba fallo ambiguo"); }
        catch (SubscriptionException ex) { check(ex is not SubscriptionCreationRejectedException, "Mercado Pago: un 500 al crear permanece ambiguo para evitar duplicados"); }
        using var stalledBody = new StalledResponseStream();
        using var stalledHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stalledBody) }))) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var callerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stalledGateway = new MercadoPagoGateway(stalledHttp, Options.Create(config), environment);
        try
        {
            await stalledGateway.GetAsync("sub-test", callerTimeout.Token);
            throw new Exception("Se esperaba un plazo de espera al leer el cuerpo de Mercado Pago");
        }
        catch (Exception ex) when (ex is SubscriptionException or OperationCanceledException)
        {
            check(ex is SubscriptionException && stalledBody.ReadStarted && !callerTimeout.IsCancellationRequested,
                "Mercado Pago: el plazo HTTP también interrumpe un cuerpo de respuesta bloqueado");
        }
        static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Environment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MiBitacora";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }

    private sealed class StalledResponseStream : Stream
    {
        public bool ReadStarted { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Gateway(Clock clock) : ISubscriptionGateway
    {
        public Dictionary<string, RemoteSubscription> Subscriptions = [];
        public Dictionary<string, RemotePayment> Payments = [];
        public List<RemoteInvoice> Invoices = [];
        public int Creates, Cancels;
        public bool FailCreateAfterAccept, RejectCreate, FailCancel;
        public string LastId = "";
        public Task<RemoteSubscription> CreateAsync(string reference, string email, long cents, CancellationToken ct)
        {
            if (RejectCreate) throw new SubscriptionCreationRejectedException();
            Creates++; LastId = "sub_" + Creates;
            var subscription = new RemoteSubscription(LastId, reference, "42", "pending", cents, "ARS", 1, "months", clock.GetUtcNow().UtcDateTime,
                "https://www.mercadopago.com.ar/subscriptions/checkout?preapproval_id=" + LastId);
            Subscriptions[LastId] = subscription;
            if (FailCreateAfterAccept) throw new HttpRequestException("Synthetic timeout after remote acceptance");
            return Task.FromResult(subscription);
        }
        public Task<RemoteSubscription> GetAsync(string id, CancellationToken ct) => Task.FromResult(Subscriptions[id]);
        public Task<RemoteSubscription?> FindAsync(string reference, CancellationToken ct) => Task.FromResult(Subscriptions.Values.SingleOrDefault(s => s.ExternalReference == reference));
        public Task<RemoteInvoice> GetInvoiceAsync(string id, CancellationToken ct) => Task.FromResult(Invoices.Single(i => i.Id == id));
        public Task<IReadOnlyList<RemoteInvoice>> GetInvoicesAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteInvoice>>(Invoices.Where(i => i.SubscriptionId == id).ToArray());
        public Task<RemotePayment> GetPaymentAsync(string id, CancellationToken ct) => Task.FromResult(Payments[id]);
        public Task CancelAsync(string id, CancellationToken ct)
        {
            if (FailCancel) throw new HttpRequestException("Synthetic cancellation outage");
            Cancels++; Subscriptions[id] = Subscriptions[id] with { Status = "cancelled" }; return Task.CompletedTask;
        }
        public void AddPayment(string subscriptionId, string id, DateTime debit)
        {
            var subscription = Subscriptions[subscriptionId];
            Invoices.Add(new("invoice_" + id, subscriptionId, id, debit));
            Payments[id] = new(id, "42", subscription.ExternalReference, "approved", subscription.AmountCents, "ARS", clock.GetUtcNow().UtcDateTime);
        }
    }
}
