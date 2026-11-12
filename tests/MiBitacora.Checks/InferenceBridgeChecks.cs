using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiBitacora.LocalInferenceAgent;
using MiBitacora.Services;

internal static class InferenceBridgeChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["LocalInferenceBridge:Enabled"] = "true", ["LocalInferenceBridge:TokenHash"] = hash });
        builder.AddLocalInferenceBridge();
        builder.Services.AddHttpClient();
        builder.Services.AddOptions<OllamaOptions>();
        builder.Services.AddOptions<EmbeddingOptions>();
        await using var app = builder.Build();
        app.MapLocalInferenceEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        var bridge = app.Services.GetRequiredService<LocalInferenceBridge>();
        var agentId = Guid.NewGuid();
        var settings = new AgentSettings { ServerUrl = "https://example.test", Token = token };
        check(bridge.Authorizes("Bearer " + token) && !bridge.Authorizes("Bearer " + new string('x', 43)) &&
            !bridge.Authorizes("") && !bridge.Authorizes("Bearer " + token + ",another"),
            "Puente privado: verifica la credencial contra su hash y rechaza ausencia o cambios");
        using (var disabled = new LocalInferenceBridge(Options.Create(new LocalInferenceBridgeOptions { TokenHash = hash })))
            check(!disabled.Authorizes("Bearer " + token) && !disabled.Connect(agentId), "Puente privado: está desactivado por defecto");

        var anonymous = Context("GET", "/status", authorized: false);
        await Invoke("/status", anonymous);
        check(anonymous.Response.StatusCode == 404 && anonymous.Response.Headers.CacheControl == "private, no-store",
            "HTTP puente: la consulta sin credencial no revela el estado ni permite cachearlo");
        using var client = new HttpClient(new LocalInferenceBridgeHandler(bridge)) { Timeout = Timeout.InfiniteTimeSpan };
        try { await client.GetAsync("http://127.0.0.1:11434/api/tags"); throw new Exception("An absent agent accepted work"); }
        catch (HttpRequestException) { check(true, "Puente privado: falla de inmediato cuando la PC está desconectada"); }
        check(bridge.Connect(agentId) && !bridge.Connect(Guid.NewGuid()), "Puente privado: una sola instancia puede retirar trabajo");

        var sent = client.GetAsync("http://127.0.0.1:11434/api/tags");
        var poll = Context("GET", "/jobs/next");
        await Invoke("/jobs/next", poll);
        var job = JsonSerializer.Deserialize<BridgeRequest>(Bytes(poll), BridgeProtocol.Json)!;
        check(poll.Response.StatusCode == 200 && job.Method == "GET" && job.Path == "/api/tags" && job.Body.Length == 0,
            "HTTP puente: retira únicamente el trabajo pendiente del servidor");
        var localCalls = 0;
        using var local = new HttpClient(new Handler((request, _) =>
        {
            localCalls++;
            check(request.RequestUri == new Uri("http://127.0.0.1:11434/api/tags") && request.Headers.Authorization is null,
                "Agente: el destino es loopback y la credencial del puente nunca se envía a Ollama");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { models = new[] { new { name = "embeddinggemma:latest" } } }) });
        }));
        using var unusedRemote = new HttpClient(new Handler((_, _) => throw new Exception("Unexpected remote operation")));
        var agent = new InferenceAgent(settings, unusedRemote, local, _ => { });
        var forwarded = await agent.ForwardAsync(job, CancellationToken.None);
        var foreign = Context("POST", "/jobs/result", forwarded.Body); foreign.Request.Headers[BridgeProtocol.AgentHeader] = Guid.NewGuid().ToString();
        foreign.Request.RouteValues["id"] = job.Id.ToString(); foreign.Request.RouteValues["status"] = "200";
        await Invoke("/jobs/{id:guid}/results/{status:int}", foreign);
        check(foreign.Response.StatusCode == 404 && !sent.IsCompleted, "HTTP puente: otro agente no puede completar un trabajo retirado");
        var oversized = Context("POST", "/jobs/result"); oversized.Request.ContentLength = BridgeProtocol.MaxResponseBytes + 1L;
        oversized.Request.RouteValues["id"] = job.Id.ToString(); oversized.Request.RouteValues["status"] = "200";
        await Invoke("/jobs/{id:guid}/results/{status:int}", oversized);
        check(oversized.Response.StatusCode == 413 && !sent.IsCompleted, "HTTP puente: rechaza respuestas demasiado grandes antes de leerlas");
        var completed = Context("POST", "/jobs/result", forwarded.Body);
        completed.Request.RouteValues["id"] = job.Id.ToString(); completed.Request.RouteValues["status"] = "200";
        await Invoke("/jobs/{id:guid}/results/{status:int}", completed);
        using var received = await sent.WaitAsync(TimeSpan.FromSeconds(2));
        check(completed.Response.StatusCode == 204 && (await received.Content.ReadAsByteArrayAsync()).SequenceEqual(forwarded.Body),
            "HTTP puente: la respuesta del modelo llega sin alteraciones al cliente original");
        var duplicate = Context("POST", "/jobs/result", forwarded.Body);
        duplicate.Request.RouteValues["id"] = job.Id.ToString(); duplicate.Request.RouteValues["status"] = "200";
        await Invoke("/jobs/{id:guid}/results/{status:int}", duplicate);
        check(duplicate.Response.StatusCode == 404, "HTTP puente: un resultado duplicado no vuelve a completar ni recrea trabajo");

        using var cancellation = new CancellationTokenSource();
        var cancelled = client.GetAsync("http://127.0.0.1:11434/api/tags", cancellation.Token);
        var cancelledJob = await bridge.TakeAsync(agentId, CancellationToken.None);
        cancellation.Cancel();
        try { await cancelled; throw new Exception("Missing caller cancellation"); } catch (OperationCanceledException) { }
        check(cancelledJob is not null && !bridge.IsRunning(cancelledJob.Id, agentId) &&
            !bridge.Complete(cancelledJob.Id, agentId, forwarded), "Puente privado: cancelar retira el trabajo y descarta resultados tardíos");
        var rejectedDestinations = 0;
        foreach (var uri in new[] { "http://192.168.0.2/api/tags", "https://127.0.0.1/api/tags", "http://127.0.0.1/api/pull",
            "http://user@127.0.0.1/api/tags", "http://127.0.0.1/api/tags?other=1" })
        {
            try { await client.GetAsync(uri); } catch (HttpRequestException) { rejectedDestinations++; }
        }
        check(rejectedDestinations == 5, "Puente privado: rechaza destinos remotos, credenciales en URL y administración de modelos");
        using (var large = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/api/chat") { Content = new ByteArrayContent([]) })
        {
            large.Content.Headers.ContentLength = BridgeProtocol.MaxRequestBytes + 1L;
            try { await client.SendAsync(large); throw new Exception("An oversized request was accepted"); }
            catch (HttpRequestException) { check(true, "Puente privado: limita también los cuerpos enviados a la PC"); }
        }
        var rejectedModels = 0;
        foreach (var invalid in new[]
        {
            job with { Method = "POST", Path = "/api/pull", Body = "{}"u8.ToArray() },
            job with { Method = "POST", Path = "/api/chat", Body = "{\"model\":\"remote-cloud\",\"stream\":false}"u8.ToArray() },
            job with { Method = "POST", Path = "/api/chat", Body = "{\"model\":\"gemma4:12b\",\"stream\":true}"u8.ToArray() },
            job with { Method = "POST", Path = "/api/embed", Body = "{\"model\":\"another-model\"}"u8.ToArray() }
        })
        {
            try { await agent.ForwardAsync(invalid, CancellationToken.None); } catch (InvalidDataException) { rejectedModels++; }
        }
        check(rejectedModels == 4 && localCalls == 1, "Agente: no descarga modelos ni acepta modelos cloud, streaming o endpoints ajenos");
        var badOrigins = 0;
        foreach (var origin in new[] { "http://example.test", "https://example.test/path", "https://user@example.test", "https://example.test/?key=x" })
        {
            try { _ = new AgentSettings { ServerUrl = origin, Token = token }.ServerUri(); } catch (InvalidDataException) { badOrigins++; }
        }
        check(badOrigins == 4, "Agente: exige un origen HTTPS sin rutas, secretos en URL ni redirecciones");

        using (var queueCancellation = new CancellationTokenSource())
        {
            var queued = Enumerable.Range(0, 16).Select(_ => client.GetAsync("http://127.0.0.1/api/tags", queueCancellation.Token)).ToArray();
            try { await client.GetAsync("http://127.0.0.1/api/tags"); throw new Exception("The queue exceeded its capacity"); }
            catch (HttpRequestException) { check(true, "Puente privado: acota la cola cuando el agente no puede retirar más trabajo"); }
            queueCancellation.Cancel();
            try { await Task.WhenAll(queued); } catch (OperationCanceledException) { }
            check(queued.All(t => t.IsCanceled), "Puente privado: una cancelación libera todas las esperas de una cola llena");
        }

        using var cancelledLocal = new HttpClient(new Handler(async (_, ct) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new Exception("The model was not cancelled"); }));
        var resultPosts = 0;
        using var cancelledRemote = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) resultPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var cancelledAgent = new InferenceAgent(settings, cancelledRemote, cancelledLocal, _ => { });
        try { await cancelledAgent.ExecuteAsync(job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)); throw new Exception("The abandoned model call remained active"); }
        catch (OperationCanceledException) { check(resultPosts == 0, "Agente: cancela Ollama cuando el servidor abandona la solicitud"); }

        DefaultHttpContext Context(string method, string path, byte[]? body = null, bool authorized = true)
        {
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.Request.Method = method; context.Request.Scheme = "https"; context.Request.Path = BridgeProtocol.Prefix + path;
            context.Request.Host = new HostString("example.test");
            if (authorized) context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.Headers[BridgeProtocol.AgentHeader] = agentId.ToString();
            context.Request.ContentType = "application/json"; context.Request.ContentLength = body?.Length ?? 0;
            context.Request.Body = new MemoryStream(body ?? []); context.Response.Body = new MemoryStream();
            return context;
        }
        Task Invoke(string path, HttpContext context) => endpoints.Single(e => e.RoutePattern.RawText == BridgeProtocol.Prefix + path).RequestDelegate!(context);
        static byte[] Bytes(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
