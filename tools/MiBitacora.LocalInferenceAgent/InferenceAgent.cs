using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MiBitacora.Services;

namespace MiBitacora.LocalInferenceAgent;

public sealed class AgentSettings
{
    public string ServerUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string OllamaExe { get; set; } = "";
    public string ModelsPath { get; set; } = "";
    public bool StartOllama { get; set; } = true;
    public bool AllowLoopbackHttpForTesting { get; set; }
    public Uri ServerUri()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(AllowLoopbackHttpForTesting && uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            Token.Length != 43 || !Token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidDataException("The agent requires an HTTPS origin and its private random credential.");
        return uri;
    }
}

public sealed class InferenceAgent(AgentSettings settings, HttpClient remote, HttpClient local, Action<string> log)
{
    private readonly Uri _server = settings.ServerUri();
    private readonly Guid _agent = Guid.NewGuid();
    private DateTime _lastLog;

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PollAsync(ct)));

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, BridgeProtocol.Prefix + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        request.Headers.Add(BridgeProtocol.AgentHeader, _agent.ToString());
        return request;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var request = Request(HttpMethod.Get, "/jobs/next");
                using var response = await remote.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pollTimeout.Token);
                if (response.StatusCode == HttpStatusCode.NoContent) continue;
                response.EnsureSuccessStatusCode();
                var bytes = await BridgeProtocol.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(pollTimeout.Token),
                    BridgeProtocol.MaxRequestBytes * 4 / 3 + 4096, pollTimeout.Token);
                var job = JsonSerializer.Deserialize<BridgeRequest>(bytes, BridgeProtocol.Json)
                    ?? throw new InvalidDataException("Missing inference job.");
                await ExecuteAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // Neither request/response bodies nor the bearer credential belong in diagnostics.
                if (DateTime.UtcNow - _lastLog > TimeSpan.FromMinutes(1))
                {
                    _lastLog = DateTime.UtcNow;
                    log("Waiting for the private web bridge or local model service.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public async Task ExecuteAsync(BridgeRequest job, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = job.ExpiresUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return;
        lifetime.CancelAfter(remaining < TimeSpan.FromMinutes(10) ? remaining : TimeSpan.FromMinutes(10));
        var watcher = WatchAsync(job.Id, lifetime);
        try
        {
            BridgeResponse result;
            try { result = await ForwardAsync(job, lifetime.Token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                result = new(503, "application/json", "{\"error\":\"Local model service unavailable.\"}"u8.ToArray());
            }
            using var resultTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            resultTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var post = Request(HttpMethod.Post, $"/jobs/{job.Id}/results/{result.Status}");
            post.Content = new ByteArrayContent(result.Body);
            post.Content.Headers.ContentType = new(result.ContentType);
            using var accepted = await remote.SendAsync(post, resultTimeout.Token);
            if (accepted.StatusCode != HttpStatusCode.NotFound) accepted.EnsureSuccessStatusCode();
        }
        finally
        {
            await lifetime.CancelAsync();
            await watcher.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task WatchAsync(Guid id, CancellationTokenSource lifetime)
    {
        var lastContact = DateTime.UtcNow;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = Request(HttpMethod.Get, $"/jobs/{id}");
                using var response = await remote.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Conflict)
                { await lifetime.CancelAsync(); return; }
                response.EnsureSuccessStatusCode();
                lastContact = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (DateTime.UtcNow - lastContact > TimeSpan.FromSeconds(25)) await lifetime.CancelAsync();
            }
        }
    }

    public async Task<BridgeResponse> ForwardAsync(BridgeRequest job, CancellationToken ct)
    {
        if (!BridgeProtocol.Allows(job.Method, job.Path) || job.Body.Length > BridgeProtocol.MaxRequestBytes ||
            job.ContentType?.Contains('\r') == true || job.ContentType?.Contains('\n') == true)
            throw new InvalidDataException("Rejected inference destination or payload.");
        if (job.Path is "/api/chat" or "/api/embed")
        {
            using var json = JsonDocument.Parse(job.Body);
            var model = json.RootElement.GetProperty("model").GetString();
            if ((job.Path == "/api/embed" && model is not ("embeddinggemma" or "embeddinggemma:latest")) ||
                (job.Path == "/api/chat" && (model is not ("gemma4:12b" or "qwen3-vl:4b-instruct") ||
                    !json.RootElement.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.False)))
                throw new InvalidDataException("Rejected inference model or streaming request.");
        }
        using var request = new HttpRequestMessage(new HttpMethod(job.Method), new Uri("http://127.0.0.1:11434" + job.Path));
        if (job.Method == "POST")
        {
            request.Content = new ByteArrayContent(job.Body);
            if (job.ContentType is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(job.ContentType);
        }
        using var response = await local.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Content.Headers.ContentLength > BridgeProtocol.MaxResponseBytes) throw new InvalidDataException("Oversized inference response.");
        var body = await BridgeProtocol.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), BridgeProtocol.MaxResponseBytes, ct);
        return new((int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType is "application/json" ? "application/json" : "text/plain", body);
    }
}
