using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiBitacora.Components.Account.Pages.Manage;
using MiBitacora.Data;
using MiBitacora.Services;

// Execute the real minimal-API delegates and Razor components in process: no sockets or payment accounts.
internal static class BillingHttpChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-billing-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root, check); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyAsync(string root, Action<bool, string> check)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={root}/billing.db;Pooling=False;Foreign Keys=True"));
        builder.Services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery();
        builder.Services.AddRazorComponents();
        builder.Services.AddSingleton<NavigationManager, Navigation>();
        var settings = new SubscriptionOptions { Provider = "Simulated", MonthlyPriceArs = 8000, PublicBaseUrl = "http://localhost:5264" };
        builder.Services.AddSingleton<IOptions<SubscriptionOptions>>(Options.Create(settings));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(TimeZoneInfo.Utc);
        builder.Services.AddSingleton<BillingGate>();
        builder.Services.AddScoped<SubscriptionService>();
        builder.Services.AddScoped<BillingWebhookInbox>();
        builder.Services.AddSingleton<ISubscriptionGateway, NoNetwork>();
        await using var app = builder.Build();
        app.MapSubscriptionEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        check(endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("/Account/")).All(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null),
            "HTTP suscripciones: checkout, cancelación y simulación requieren autenticación");
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new ApplicationUser { Id = "http-a", UserName = "http-a", Email = "a@example.test" },
                new ApplicationUser { Id = "http-b", UserName = "http-b", Email = "b@example.test" });
            await db.SaveChangesAsync();
        }
        var billing = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
        var endpoint = endpoints.Single(e => e.RoutePattern.RawText == "/Account/Subscription/{action}");
        var noCsrf = Context("http-a", "accept=yes"); noCsrf.Request.RouteValues["action"] = "Subscribe";
        await endpoint.RequestDelegate!(noCsrf);
        check(noCsrf.Response.StatusCode == 400 && await billing.GetSummaryAsync("http-a") is null,
            "HTTP suscripciones: un POST sin antiforgery no crea contrataciones");
        var subscribe = Context("http-a", "accept=yes"); subscribe.Request.RouteValues["action"] = "Subscribe";
        AddToken(subscribe);
        await endpoint.RequestDelegate!(subscribe);
        check(subscribe.Response.StatusCode == 302 && subscribe.Response.Headers.Location.ToString().StartsWith("/Account/Subscription/Simulate/"),
            "HTTP suscripciones: el POST válido redirige al checkout simulado de esa cuenta");
        var own = (await billing.GetSummaryAsync("http-a"))!;
        var simulation = endpoints.Single(e => e.RoutePattern.RawText == "/Account/Subscription/Simulate/{id:guid}/{action}");
        var foreign = Context("http-b", ""); foreign.Request.RouteValues["id"] = own.Id.ToString(); foreign.Request.RouteValues["action"] = "approve";
        AddToken(foreign); await simulation.RequestDelegate!(foreign);
        check(!(await billing.GetAccessAsync("http-a")).IsPaid && !(await billing.GetAccessAsync("http-b")).IsPaid,
            "HTTP suscripciones: no se puede aprobar el checkout de otra cuenta");
        var approve = Context("http-a", ""); approve.Request.RouteValues["id"] = own.Id.ToString(); approve.Request.RouteValues["action"] = "approve";
        AddToken(approve); await simulation.RequestDelegate!(approve);
        check((await billing.GetAccessAsync("http-a")).IsPaid, "HTTP suscripciones: aprobar un pago propio cambia el acceso sin recargar datos manualmente");

        var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        await using (renderer)
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<RenderPlan>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(RenderPlan.Context)] = Context("http-a", "") }))).ToHtmlString());
            check(html.Contains("Plan Profundo") && html.Contains("Acceso pago hasta") && html.Contains("Confirmar cancelación") && html.Contains("simulados"),
                "UI suscripciones: muestra vigencia, cancelación y aviso de cobros simulados");
        }
        var cancel = Context("http-a", ""); cancel.Request.RouteValues["action"] = "Cancel"; AddToken(cancel);
        await endpoint.RequestDelegate!(cancel);
        check((await billing.GetAccessAsync("http-a")).IsPaid && (await billing.GetSummaryAsync("http-a"))?.Status == "cancelled",
            "HTTP suscripciones: cancelar conserva el período pago");

        settings.Provider = "MercadoPago"; settings.AllowLiveRequests = true; settings.WebhookSecret = "synthetic-webhook-secret";
        var webhook = endpoints.Single(e => e.RoutePattern.RawText == "/api/mercadopago/webhook");
        var content = JsonSerializer.Serialize(new { type = "payment", data = new { id = "123" } });
        var invalid = Context(null, content, true); invalid.Request.QueryString = new QueryString("?data.id=123");
        await webhook.RequestDelegate!(invalid);
        check(invalid.Response.StatusCode == 401, "HTTP webhooks: firma ausente no ingresa a la bandeja");
        var requestId = "test-request"; var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signed = "ts=" + ts + ",v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.WebhookSecret),
            Encoding.UTF8.GetBytes($"id:123;request-id:{requestId};ts:{ts};")));
        for (var i = 0; i < 2; i++)
        {
            var received = Context(null, content, true); received.Request.QueryString = new QueryString("?data.id=123");
            received.Request.Headers["x-request-id"] = requestId; received.Request.Headers["x-signature"] = signed;
            await webhook.RequestDelegate!(received);
            check(received.Response.StatusCode == 200, "HTTP webhooks: confirma la recepción durable de una entrega firmada");
        }
        await using (var db = await factory.CreateDbContextAsync())
            check(await db.BillingWebhooks.CountAsync() == 1, "HTTP webhooks: dos entregas idénticas conservan un único evento");
        var oversized = Context(null, new string('x', 20_000), true);
        await webhook.RequestDelegate!(oversized);
        check(oversized.Response.StatusCode == 413, "HTTP webhooks: rechaza cuerpos demasiado grandes");
        check(NoNetwork.Calls == 0, "HTTP y UI: todas las pruebas terminan sin contactar Mercado Pago");

        DefaultHttpContext Context(string? user, string body, bool json = false)
        {
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (user is not null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
            context.Request.Method = "POST"; context.Request.Scheme = "http"; context.Request.Host = new HostString("localhost", 5264);
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            context.Request.ContentType = json ? "application/json" : "application/x-www-form-urlencoded";
            var bytes = Encoding.UTF8.GetBytes(body); context.Request.ContentLength = bytes.Length; context.Request.Body = new MemoryStream(bytes);
            context.Response.Body = new MemoryStream();
            return context;
        }
        void AddToken(DefaultHttpContext request)
        {
            var get = Context(null, ""); get.User = request.User; get.Request.Method = "GET";
            var token = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetAndStoreTokens(get);
            request.Request.Headers.Cookie = get.Response.Headers.SetCookie.ToString().Split(';')[0];
            request.Request.Headers["RequestVerificationToken"] = token.RequestToken;
        }
    }

    private sealed class RenderPlan : ComponentBase
    {
        [Parameter] public HttpContext Context { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<HttpContext>>(0); builder.AddAttribute(1, "Value", Context);
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(child => { child.OpenComponent<Subscription>(0); child.CloseComponent(); }));
            builder.CloseComponent();
        }
    }
    private sealed class Navigation : NavigationManager { public Navigation() => Initialize("http://localhost:5264/", "http://localhost:5264/Account/Manage/Subscription"); }
    private sealed class NoNetwork : ISubscriptionGateway
    {
        public static int Calls;
        private static Exception Fail() { Calls++; return new Exception("No network allowed in local billing tests"); }
        public Task<RemoteSubscription> CreateAsync(string r, string e, long a, CancellationToken ct) => throw Fail();
        public Task<RemoteSubscription> GetAsync(string id, CancellationToken ct) => throw Fail();
        public Task<RemoteSubscription?> FindAsync(string r, CancellationToken ct) => throw Fail();
        public Task<RemoteInvoice> GetInvoiceAsync(string id, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<RemoteInvoice>> GetInvoicesAsync(string id, CancellationToken ct) => throw Fail();
        public Task<RemotePayment> GetPaymentAsync(string id, CancellationToken ct) => throw Fail();
        public Task CancelAsync(string id, CancellationToken ct) => throw Fail();
    }
}
