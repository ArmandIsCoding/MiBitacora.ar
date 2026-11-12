using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class VectorMemoryWorker(IServiceScopeFactory scopes, IOptions<EmbeddingOptions> options,
    ILogger<VectorMemoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var unavailable = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                var client = scope.ServiceProvider.GetRequiredService<IJournalEmbeddingClient>();
                var model = await client.GetModelAsync(stoppingToken);
                if (unavailable) logger.LogInformation("La memoria semántica local volvió a estar disponible.");
                unavailable = false;
                List<string> users;
                await using (var db = await factory.CreateDbContextAsync(stoppingToken))
                {
                    var now = DateTime.UtcNow.Ticks;
                    users = await db.JournalVectorStates.AsNoTracking().Where(s =>
                        (s.IndexedRevision != s.Revision || s.ModelKey != model.Key) && s.NextAttemptTicks <= now)
                        .OrderBy(s => s.NextAttemptTicks).ThenBy(s => s.UserId).Take(8)
                        .Select(s => s.UserId).ToListAsync(stoppingToken);
                }
                var memory = scope.ServiceProvider.GetRequiredService<VectorMemoryService>();
                foreach (var user in users)
                {
                    try
                    {
                        var result = await memory.IndexPendingAsync(user, model, ct: stoppingToken);
                        if (result.Embedded > 0 || result.Complete)
                            logger.LogInformation("Indexación local: {Embedded} fragmentos guardados; pendiente {Pending}.", result.Embedded, !result.Complete);
                    }
                    catch (JournalInputException) { /* A concurrent edit is queued again by the database triggers. */ }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        // Do not log exception bodies: HTTP/database errors can contain private input.
                        logger.LogWarning("Se reintentará la indexación local ({ErrorType}).", ex.GetType().Name);
                        await using var db = await factory.CreateDbContextAsync(stoppingToken);
                        var state = await db.JournalVectorStates.SingleOrDefaultAsync(s => s.UserId == user, stoppingToken);
                        if (state is null) continue;
                        state.Failures = Math.Min(state.Failures + 1, 10);
                        state.NextAttemptTicks = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * (1 << state.Failures))).Ticks;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!unavailable) logger.LogWarning("La memoria semántica local está pendiente ({ErrorType}); la búsqueda por palabras sigue disponible.", ex.GetType().Name);
                unavailable = true;
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

public static class VectorMemoryRegistration
{
    public static void AddVectorMemory(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<EmbeddingOptions>().Bind(builder.Configuration.GetSection("Embeddings"))
            .Validate(o => o.IsValid(), "Embeddings requiere un modelo local, una URL HTTP de loopback y límites válidos.")
            .ValidateOnStart();
        builder.Services.AddSingleton<EmbeddingGate>();
        builder.Services.AddHttpClient<IJournalEmbeddingClient, OllamaEmbeddingClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .UseLocalInferenceTransport();
        builder.Services.AddScoped<VectorMemoryService>();
        builder.Services.AddHostedService<VectorMemoryWorker>();
    }
}
