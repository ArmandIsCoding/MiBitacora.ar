using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Components;
using MiBitacora.Components.Account;
using MiBitacora.Data;
using MiBitacora.Services;
using System.Threading.RateLimiting;

if (args.FirstOrDefault() == "--local-pdf-worker")
{
    Environment.ExitCode = await LocalPdfWorker.RunAsync(args);
    return;
}

var prepareImages = args.Contains("--prepare-local-images", StringComparer.Ordinal);
var checkBilling = args.Any(arg => arg is "--check-billing" or "--check-merchant" or "--check-mail");
var appArgs = args.Where(arg => arg is not ("--prepare-local-images" or "--check-billing" or "--check-merchant" or "--check-mail")).ToArray();
var builder = WebApplication.CreateBuilder(appArgs);
// Private deployment configuration: excluded from Git/publish and outside static wwwroot.
builder.Configuration.AddJsonFile("App_Data/ai.settings.json", optional: true, reloadOnChange: false);
if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(appArgs);
if (checkBilling) {
    Environment.ExitCode = await BillingReadiness.RunAsync(builder.Configuration, args.Contains("--check-merchant", StringComparer.Ordinal), args.Contains("--check-mail", StringComparer.Ordinal));
    return;
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => {
        var path = context.Request.Path.Value ?? "";
        var sharedView = context.Request.Path.StartsWithSegments("/compartido", StringComparison.OrdinalIgnoreCase);
        var limited = sharedView || HttpMethods.IsPost(context.Request.Method) &&
            (path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase) || path is "/baja" or "/arrepentimiento" or "/soporte" || path.StartsWith("/solicitud/", StringComparison.OrdinalIgnoreCase));
        return limited ? RateLimitPartition.GetFixedWindowLimiter(
            (sharedView ? "share:" : "account:") + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = sharedView ? 60 : 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }) : RateLimitPartition.GetNoLimiter("other");
    });
});
var protection = builder.Services.AddDataProtection().SetApplicationName("MiBitacora")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath,
        builder.Environment.IsDevelopment() ? ".artifacts" : "App_Data", "data-protection")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
var administratorIds = (builder.Configuration.GetSection("Administration:UserIds").Get<string[]>() ?? [])
    .Where(id => !string.IsNullOrWhiteSpace(id))
    .Select(id => id.Trim())
    .ToHashSet(StringComparer.Ordinal);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminAccess.Policy, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId && administratorIds.Contains(userId)));
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var configuredConnection = builder.Configuration.GetConnectionString("DefaultConnection");
var sqlite = configuredConnection is null
    ? new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dataDirectory, "mi-bitacora.db"),
        DefaultTimeout = 15
    }
    : new SqliteConnectionStringBuilder(configuredConnection);
sqlite.ForeignKeys = true;

builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(sqlite.ConnectionString));
builder.Services.AddSingleton(provider => new UploadFileStore(
    Path.Combine(builder.Environment.ContentRootPath, "uploads"),
    provider.GetRequiredService<ILogger<UploadFileStore>>()));
builder.Services.AddScoped<IJournalStore, JournalStore>();
builder.Services.AddScoped<ThoughtMapService>();
builder.Services.AddScoped<TherapyShareBuilder>();
builder.Services.AddScoped<TherapyShareService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HeicImageService>();
builder.Services.AddSingleton(new AttachmentPreparationService(
    Path.Combine(dataDirectory, "media-staging"), Path.Combine(builder.Environment.ContentRootPath, "media-tools")));
builder.Services.AddOptions<LocalAttachmentOptions>().Bind(builder.Configuration.GetSection("LocalAttachments"))
    .Validate(options => options.IsValid(), "Los límites de lectura local no son válidos.").ValidateOnStart();
builder.Services.AddSingleton(sp => new LocalAttachmentProcessor(Path.Combine(dataDirectory, "local-analysis"),
    Path.Combine(builder.Environment.ContentRootPath, "media-tools"), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalAttachmentOptions>>()));
builder.Services.AddScoped<LocalAttachmentAnalysis>();
builder.Services.AddOptions<JournalStorageOptions>()
    .Bind(builder.Configuration.GetSection("Journal"))
    .Validate(options => options.MaxAccountAttachmentBytes > 0 && options.MaxAccountFiles > 0,
        "Las cuotas de adjuntos deben ser positivas.")
    .ValidateOnStart();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<AdminDashboardService>();
builder.Services.AddSingleton(TimeZoneInfo.FindSystemTimeZoneById(
    builder.Configuration["Journal:TimeZone"] ?? "America/Argentina/Buenos_Aires"));
builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection("Ollama"))
    .Validate(options => options.IsValid(), "Ollama requiere una URL HTTP de loopback, un modelo local y límites válidos.")
    .ValidateOnStart();
builder.Services.AddSingleton<LocalInferenceGate>();
builder.Services.AddKeyedSingleton<LocalInferenceGate>("OpenAI");
builder.AddLocalInferenceBridge();
builder.AddBilling();
builder.AddVectorMemory();
builder.Services.AddOptions<JournalAiOptions>().Bind(builder.Configuration.GetSection("AI"))
    .Validate(options => options.Provider is "Ollama" or "OpenAI", "AI:Provider debe ser Ollama u OpenAI.")
    .ValidateOnStart();
// Cloud credentials are validated when the selected provider is called.
builder.Services.AddOptions<OpenAiOptions>().Bind(builder.Configuration.GetSection("OpenAI"));
builder.Services.AddSingleton<JournalTopicJobs>();
builder.Services.AddScoped<JournalTopicService>();
builder.Services.AddSingleton<JournalMemoryJobs>();
builder.Services.AddSingleton<JournalConversationJobs>();
builder.Services.AddScoped<JournalConversationService>();
builder.Services.AddOptions<AudioTranscriptionOptions>().Bind(builder.Configuration.GetSection("AudioTranscription"))
    .Validate(options => options.IsValid(), "La transcripción requiere gpt-transcribe y un plazo válido.").ValidateOnStart();
builder.Services.AddHttpClient("AccountOpenAI", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddHttpClient("AccountOllama", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .UseLocalInferenceTransport();
builder.Services.AddScoped<OllamaModelClient>(sp => new(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("AccountOllama"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>(),
    sp.GetRequiredService<LocalInferenceGate>(), sp.GetRequiredService<ILogger<OllamaModelClient>>()));
builder.Services.AddScoped<OpenAiModelClient>(sp => new(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("AccountOpenAI"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenAiOptions>>(),
    sp.GetRequiredKeyedService<LocalInferenceGate>("OpenAI"), sp.GetRequiredService<ILogger<OpenAiModelClient>>()));
builder.Services.AddScoped<OpenAiAudioTranscriber>(sp => new(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("AccountOpenAI"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenAiOptions>>(),
    Microsoft.Extensions.Options.Options.Create(new JournalAiOptions { Provider = "OpenAI" }),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AudioTranscriptionOptions>>(),
    sp.GetRequiredKeyedService<LocalInferenceGate>("OpenAI"), sp.GetRequiredService<ILogger<OpenAiAudioTranscriber>>()));
builder.Services.AddScoped<OllamaAudioTranscriber>(sp => new(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("AccountOllama"),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>(), sp.GetRequiredService<LocalInferenceGate>()));
builder.Services.AddScoped<AccountAiRouter>();
builder.Services.AddScoped<IAudioTranscriber, AccountAudioTranscriber>();
builder.Services.AddScoped<JournalMemoryService>();
builder.Services.AddScoped<JournalReportService>();
builder.Services.AddHttpClient<OllamaJournalService>(client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .UseLocalInferenceTransport();
builder.Services.AddHttpClient<OpenAiJournalService>(client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    });

builder.Services.AddScoped<JournalAiService, AccountJournalService>();
builder.Services.AddScoped<IJournalReflector>(provider => provider.GetRequiredService<JournalAiService>());
builder.Services.AddScoped<IJournalTopicAnalyzer>(provider => provider.GetRequiredService<JournalAiService>());
builder.Services.AddScoped<IJournalMemoryAnalyzer>(provider => provider.GetRequiredService<JournalAiService>());
builder.Services.AddScoped<IJournalReportAnalyzer>(provider => provider.GetRequiredService<JournalAiService>());

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = AccountText.MinimumPasswordLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(2));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "MiBitacora.Session";
    options.Cookie.HttpOnly = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

var app = builder.Build();
app.Services.GetRequiredService<AttachmentPreparationService>().CleanupInterrupted();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var uploads = scope.ServiceProvider.GetRequiredService<UploadFileStore>();
    var exported = await LegacyUploadMigration.ExportAsync(db, uploads);
    await db.Database.MigrateAsync();
    if (exported) await db.Database.ExecuteSqlRawAsync("VACUUM;");
    await uploads.CleanupOrphansAsync(db);
}

if (prepareImages)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var images = scope.ServiceProvider.GetRequiredService<HeicImageService>();
    var originals = await db.JournalAttachments.Where(row => row.ContentType == "image/heic" || row.ContentType == "image/heif")
        .Select(row => new { row.Id, row.Entry.UserId }).ToListAsync();
    var prepared = 0;
    foreach (var original in originals)
    {
        try
        {
            await using var jpeg = await images.OpenJpegAsync(original.UserId, original.Id);
            if (jpeg is not null) prepared++;
        }
        catch (JournalInputException) { /* Original retained; the converter logs the failure. */ }
        if (prepared % 10 == 0) Console.WriteLine($"Copias JPG preparadas: {prepared}/{originals.Count}");
    }
    Console.WriteLine($"Resultado: {prepared}/{originals.Count} fotos disponibles como JPG; originales conservados.");
    Environment.ExitCode = prepared == originals.Count ? 0 : 1;
    await app.DisposeAsync();
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/bitacora")
        || context.Request.Path.StartsWithSegments("/Account")
        || context.Request.Path.StartsWithSegments("/admin")
        || context.Request.Path.StartsWithSegments("/entrada")
        || context.Request.Path.StartsWithSegments("/informe")
        || context.Request.Path.StartsWithSegments("/mapa")
        || context.Request.Path.StartsWithSegments("/compartir-terapia")
        || context.Request.Path.StartsWithSegments("/compartido")
        || context.Request.Path.StartsWithSegments("/adjuntos"))
    {
        context.Response.Headers.CacheControl = "private, no-store";
    }
    if (context.Request.Path.StartsWithSegments("/compartido") || context.Request.Path.StartsWithSegments("/compartir-terapia"))
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    }
    if (context.Request.Path.StartsWithSegments("/solicitud") || context.Request.Path is { Value: "/baja" or "/arrepentimiento" or "/soporte" }) {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAdditionalIdentityEndpoints();
app.MapSubscriptionEndpoints();
app.MapBillingAdministration();
app.MapLocalInferenceEndpoints();

app.MapJournalAttachments();

app.Run();
