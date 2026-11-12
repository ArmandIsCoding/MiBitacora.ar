using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class OllamaEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> settings,
    EmbeddingGate gate) : IJournalEmbeddingClient
{
    private readonly EmbeddingOptions _options = settings.Value;
    private Uri Endpoint(string path) => new(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), path);

    public async Task<EmbeddingModel> GetModelAsync(CancellationToken ct)
    {
        if (!_options.IsValid()) throw new EmbeddingUnavailableException("La configuración de memoria local no es válida.");
        if (!_options.Enabled) throw new EmbeddingUnavailableException("La memoria semántica está desactivada.");
        return await GuardAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("api/tags"));
            using var json = await SendAsync(request, token);
            var name = _options.Model.Contains(':') ? _options.Model : _options.Model + ":latest";
            if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new EmbeddingUnavailableException("No pudimos consultar el modelo de memoria local.");
            foreach (var model in models.EnumerateArray())
            {
                if (!model.TryGetProperty("name", out var itemName) || itemName.GetString() != name) continue;
                var digest = model.GetProperty("digest").GetString() ?? "";
                if (digest.StartsWith("sha256:", StringComparison.Ordinal)) digest = digest[7..];
                if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) break;
                return new EmbeddingModel($"{JournalVectors.Format}:{_options.Dimensions}:{digest.ToLowerInvariant()}", _options.Dimensions);
            }
            throw new EmbeddingUnavailableException("El modelo de embeddings no está instalado en Ollama.");
        }, ct, 5);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingModel model, IReadOnlyList<string> inputs,
        bool interactive, CancellationToken ct)
    {
        if (inputs.Count == 0 || inputs.Count > 16 || inputs.Any(s => Encoding.UTF8.GetByteCount(s) > 1700))
            throw new ArgumentException("El lote de embeddings excede el límite local.", nameof(inputs));
        return await GuardAsync<IReadOnlyList<float[]>>(async token =>
        {
            using var lease = await gate.EnterAsync(interactive, token);
            if ((await GetModelAsync(token)).Key != model.Key) throw ChangedModel();
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/embed"))
            {
                Content = JsonContent.Create(new
                {
                    model = _options.Model, input = inputs, truncate = false, dimensions = model.Dimensions,
                    keep_alive = "10m", options = new { num_gpu = _options.UseCpu ? 0 : -1 }
                })
            };
            using var json = await SendAsync(request, token);
            var vectors = json.RootElement.GetProperty("embeddings").EnumerateArray()
                .Select(v => JournalVectors.Normalize(v.EnumerateArray().Select(n => n.GetSingle()).ToArray(), model.Dimensions)).ToArray();
            if (vectors.Length != inputs.Count) throw new EmbeddingUnavailableException("El lote de memoria quedó incompleto.");
            if ((await GetModelAsync(token)).Key != model.Key) throw ChangedModel();
            return vectors;
        }, ct);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new EmbeddingUnavailableException("Ollama no pudo calcular la memoria local.");
        const int maxBytes = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new EmbeddingUnavailableException("La respuesta de memoria excede el límite.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new EmbeddingUnavailableException("La respuesta de memoria excede el límite.");
            buffer.Write(bytes, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct, int? timeoutSeconds = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? _options.TimeoutSeconds));
        try { return await action(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EmbeddingUnavailableException("La memoria local tardó demasiado."); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IOException or FormatException)
        { throw new EmbeddingUnavailableException("No pudimos consultar la memoria local.", ex); }
    }
    private static EmbeddingUnavailableException ChangedModel() => new("El modelo de memoria cambió. Se volverá a indexar el historial.");
}
