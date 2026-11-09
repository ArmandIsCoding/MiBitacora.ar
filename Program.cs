using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Components;
using MiBitacora.Components.Account;
using MiBitacora.Data;
using MiBitacora.Services;

var prepareImages = args.Contains("--prepare-local-images", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--prepare-local-images").ToArray());

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
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
builder.Services.AddSingleton<HeicImageService>();
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
builder.Services.AddHttpClient<IJournalReflector, OllamaJournalService>(client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    });

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

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "MiBitacora.Session";
    options.Cookie.HttpOnly = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

var app = builder.Build();

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
        || context.Request.Path.StartsWithSegments("/adjuntos"))
    {
        context.Response.Headers.CacheControl = "private, no-store";
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAdditionalIdentityEndpoints();

var attachments = app.MapGroup("/adjuntos").RequireAuthorization();
attachments.MapGet("/{id:guid}", DownloadAttachment);
attachments.MapGet("/{id:guid}/vista", PreviewAttachment);

app.Run();

static async Task<IResult> DownloadAttachment(
    Guid id, ClaimsPrincipal user, IJournalStore store, HttpContext http, CancellationToken cancellationToken)
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var file = await store.GetAttachmentAsync(userId, id, cancellationToken);
    if (file is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "private, no-store";
    return Results.File(file.Content, "application/octet-stream", file.FileName);
}

static async Task<IResult> PreviewAttachment(
    Guid id, ClaimsPrincipal user, IJournalStore store, HttpContext http, CancellationToken cancellationToken)
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var file = await store.GetAttachmentPreviewAsync(userId, id, cancellationToken);
    if (file?.ContentType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/avif"))
    {
        if (file is not null) await file.Content.DisposeAsync();
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "private, no-store";
    return Results.File(file.Content, file.ContentType);
}
