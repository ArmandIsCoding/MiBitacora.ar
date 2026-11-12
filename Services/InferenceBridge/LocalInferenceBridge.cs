using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class LocalInferenceBridgeOptions
{
    public bool Enabled { get; set; }
    // Only the hash is installed on the web server; the random bearer credential stays on the PC.
    public string TokenHash { get; set; } = "";
    public bool IsValid() => !Enabled || (TokenHash.Length == 64 && TokenHash.All(Uri.IsHexDigit));
}

public sealed class LocalInferenceBridge(IOptions<LocalInferenceBridgeOptions> options) : IDisposable
{
    private readonly LocalInferenceBridgeOptions _options = options.Value;
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(16);
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly object _sync = new();
    private Guid _agent;
    private long _lastContact;
    private long _bytes;
    private long _completed;
    private bool _disposed;

    public bool IsOnline => _options.Enabled && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastContact)
        < TimeSpan.FromSeconds(35).Ticks;
    public object Status => new { Enabled = _options.Enabled, Connected = IsOnline,
        Pending = _pending.Count, Completed = Interlocked.Read(ref _completed) };

    public bool Authorizes(string authorization)
    {
        if (!_options.Enabled || !_options.IsValid() || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var token = authorization[7..];
        return token.Length is >= 43 and <= 128 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(_options.TokenHash));
    }

    public bool Connect(Guid agent)
    {
        if (agent == Guid.Empty || !_options.Enabled) return false;
        lock (_sync)
        {
            if (_disposed || (IsOnline && _agent != agent)) return false;
            _agent = agent;
            Interlocked.Exchange(ref _lastContact, DateTime.UtcNow.Ticks);
            return true;
        }
    }

    public bool Touch(Guid agent)
    {
        lock (_sync)
        {
            if (_disposed || agent == Guid.Empty || agent != _agent) return false;
            Interlocked.Exchange(ref _lastContact, DateTime.UtcNow.Ticks);
            return true;
        }
    }

    public async Task<BridgeRequest?> TakeAsync(Guid agent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var id = await _queue.Reader.ReadAsync(timeout.Token);
                if (!_pending.TryGetValue(id, out var pending) || pending.Request.ExpiresUtc <= DateTime.UtcNow) continue;
                lock (_sync)
                {
                    if (_agent != agent) return null;
                    pending.Agent = agent;
                    pending.Leased = true;
                }
                return pending.Request;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public bool IsRunning(Guid id, Guid agent) => _pending.TryGetValue(id, out var pending)
        && pending.Leased && pending.Agent == agent;

    public bool Complete(Guid id, Guid agent, BridgeResponse response)
    {
        if (response.Status is < 200 or > 599 || response.Body.Length > BridgeProtocol.MaxResponseBytes ||
            response.ContentType is not ("application/json" or "text/plain" or "application/octet-stream")) return false;
        if (!_pending.TryGetValue(id, out var pending) || !pending.Leased || pending.Agent != agent) return false;
        if (!pending.Result.TrySetResult(response)) return false;
        Interlocked.Increment(ref _completed);
        return true;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        var uri = message.RequestUri;
        if (uri is null || uri.Scheme != "http" || uri.Host is not ("127.0.0.1" or "[::1]") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !BridgeProtocol.Allows(message.Method.Method, uri.AbsolutePath))
            throw new HttpRequestException("The private inference bridge rejected the destination.");
        if (!IsOnline) throw new HttpRequestException("The private inference agent is unavailable.");
        if (message.Content?.Headers.ContentLength > BridgeProtocol.MaxRequestBytes)
            throw new HttpRequestException("The private inference request is too large.");
        byte[] body = [];
        if (message.Content is not null)
        {
            await using var stream = await message.Content.ReadAsStreamAsync(ct);
            body = await BridgeProtocol.ReadBoundedAsync(stream, BridgeProtocol.MaxRequestBytes, ct);
        }
        var request = new BridgeRequest(Guid.NewGuid(), message.Method.Method, uri.AbsolutePath,
            message.Content?.Headers.ContentType?.ToString(), body, DateTime.UtcNow.AddMinutes(10));
        var pending = new Pending(request);
        lock (_sync)
        {
            if (_disposed || _pending.Count >= 16 || _bytes + body.Length > 64L * 1024 * 1024)
                throw new HttpRequestException("The private inference queue is full.");
            _pending[request.Id] = pending;
            _bytes += body.Length;
        }
        try
        {
            if (!_queue.Writer.TryWrite(request.Id)) throw new HttpRequestException("The private inference queue is full.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            while (!pending.Result.Task.IsCompleted)
            {
                await Task.WhenAny(pending.Result.Task, Task.Delay(TimeSpan.FromSeconds(5), timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
                if (!IsOnline) throw new HttpRequestException("The private inference agent disconnected.");
            }
            var result = await pending.Result.Task;
            var response = new HttpResponseMessage((HttpStatusCode)result.Status) { Content = new ByteArrayContent(result.Body) };
            response.Content.Headers.ContentType = new(result.ContentType);
            return response;
        }
        finally
        {
            lock (_sync)
            {
                _pending.TryRemove(request.Id, out _);
                _bytes -= body.Length;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _queue.Writer.TryComplete();
            foreach (var pending in _pending.Values)
                pending.Result.TrySetException(new HttpRequestException("The inference bridge is stopping."));
        }
    }

    private sealed class Pending(BridgeRequest request)
    {
        public BridgeRequest Request { get; } = request;
        public TaskCompletionSource<BridgeResponse> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Agent;
        public bool Leased;
    }
}

public sealed class LocalInferenceBridgeHandler(LocalInferenceBridge bridge) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => bridge.SendAsync(request, ct);
}

public static class LocalInferenceTransport
{
    public static void AddLocalInferenceBridge(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<LocalInferenceBridgeOptions>().Bind(builder.Configuration.GetSection("LocalInferenceBridge"))
            .Validate(o => o.IsValid(), "El puente de IA requiere el hash SHA-256 de una credencial privada.").ValidateOnStart();
        builder.Services.AddSingleton<LocalInferenceBridge>();
    }

    public static IHttpClientBuilder UseLocalInferenceTransport(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<IOptions<LocalInferenceBridgeOptions>>().Value.Enabled
            ? new LocalInferenceBridgeHandler(sp.GetRequiredService<LocalInferenceBridge>())
            : new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
}
