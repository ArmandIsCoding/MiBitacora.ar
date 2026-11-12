using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public static class LocalInferenceEndpoints
{
    public static void MapLocalInferenceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(BridgeProtocol.Prefix).ExcludeFromDescription();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            var bridge = context.HttpContext.RequestServices.GetRequiredService<LocalInferenceBridge>();
            return bridge.Authorizes(context.HttpContext.Request.Headers.Authorization.ToString())
                ? await next(context) : Results.NotFound();
        });

        group.MapGet("/status", (LocalInferenceBridge bridge) => Results.Json(bridge.Status));
        group.MapGet("/jobs/next", async (HttpContext http, LocalInferenceBridge bridge) =>
        {
            if (!Agent(http, out var agent) || !bridge.Connect(agent)) return (IResult)Results.Conflict();
            var request = await bridge.TakeAsync(agent, http.RequestAborted);
            bridge.Touch(agent);
            return request is null ? Results.NoContent() : Results.Json(request);
        });
        group.MapGet("/jobs/{id:guid}", (Guid id, HttpContext http, LocalInferenceBridge bridge) =>
            Agent(http, out var agent) && bridge.Touch(agent) && bridge.IsRunning(id, agent)
                ? (IResult)Results.NoContent() : Results.NotFound());
        group.MapPost("/jobs/{id:guid}/results/{status:int}", async (Guid id, int status, HttpContext http, LocalInferenceBridge bridge) =>
        {
            if (!Agent(http, out var agent) || !bridge.Touch(agent) || !bridge.IsRunning(id, agent)) return (IResult)Results.NotFound();
            if (http.Request.ContentLength > BridgeProtocol.MaxResponseBytes) return Results.StatusCode(413);
            try
            {
                var body = await BridgeProtocol.ReadBoundedAsync(http.Request.Body, BridgeProtocol.MaxResponseBytes, http.RequestAborted);
                return bridge.Complete(id, agent, new(status, http.Request.ContentType?.Split(';')[0] ?? "application/octet-stream", body))
                    ? Results.NoContent() : Results.BadRequest();
            }
            catch (InvalidDataException) { return Results.StatusCode(413); }
        });
        // Fixed fictitious data only. The credential also authorizes the desktop worker, never a journal account.
        group.MapPost("/probe", async (IHttpClientFactory clients, IOptions<OllamaOptions> options,
            IOptions<EmbeddingOptions> embeddings, LocalInferenceBridge bridge, HttpContext http) =>
        {
            if (!bridge.IsOnline) return (IResult)Results.StatusCode(503);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                var client = clients.CreateClient("AccountOllama");
                var root = new Uri(options.Value.BaseUrl);
                using var tags = await client.GetFromJsonAsync<JsonDocument>(new Uri(root, "/api/tags"), timeout.Token);
                var names = tags!.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToArray();
                if (!names.Contains(options.Value.Model) || !names.Contains("embeddinggemma:latest")) return Results.StatusCode(503);
                using var vector = await client.PostAsJsonAsync(new Uri(root, "/api/embed"), new
                {
                    model = "embeddinggemma:latest", input = new[] { "Prueba técnica ficticia sin datos de un diario." },
                    truncate = false, dimensions = embeddings.Value.Dimensions, options = new { num_gpu = 0 }, keep_alive = "10m"
                }, timeout.Token);
                vector.EnsureSuccessStatusCode();
                using var vectors = await JsonDocument.ParseAsync(await vector.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var dimensions = vectors.RootElement.GetProperty("embeddings")[0].GetArrayLength();
                if (dimensions != embeddings.Value.Dimensions) return Results.StatusCode(503);
                using var answer = await client.PostAsJsonAsync(new Uri(root, "/api/chat"), new
                {
                    model = options.Value.Model, stream = false, think = false, keep_alive = "5m",
                    messages = new[] { new { role = "user", content = "Prueba técnica ficticia. Respondé únicamente con el objeto JSON: {\"ready\":true}" } },
                    format = new { type = "object", properties = new { ready = new { type = "boolean" } }, required = new[] { "ready" }, additionalProperties = false },
                    options = new { num_ctx = options.Value.ContextLength, num_predict = 32, temperature = 0 }
                }, timeout.Token);
                answer.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(await answer.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                using var content = JsonDocument.Parse(json.RootElement.GetProperty("message").GetProperty("content").GetString()!);
                return content.RootElement.GetProperty("ready").GetBoolean()
                    ? Results.Json(new { Connected = true, Generation = true, EmbeddingDimensions = dimensions, Model = options.Value.Model })
                    : Results.StatusCode(503);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            { return Results.StatusCode(503); }
        });
    }

    private static bool Agent(HttpContext http, out Guid agent) =>
        Guid.TryParse(http.Request.Headers[BridgeProtocol.AgentHeader].ToString(), out agent) && agent != Guid.Empty;
}
