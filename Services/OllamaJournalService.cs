using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class OllamaOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "gemma4:12b";
    public int ContextLength { get; set; } = 8192;
    public int MaxOutputTokens { get; set; } = 2300;
    public int TimeoutSeconds { get; set; } = 300;

    public bool IsValid() =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == "http" &&
        uri.Host is "127.0.0.1" or "[::1]" &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 &&
        (Model is "gemma4:12b" or "qwen3-vl:4b-instruct") &&
        ContextLength is >= 8192 and <= 16384 &&
        MaxOutputTokens is >= 512 and <= 2300 &&
        TimeoutSeconds is >= 30 and <= 600;
}

// Shared across all Blazor circuits. An abandoned request must release its slot.
public sealed class LocalInferenceGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    private int _requests;
    internal bool IsBusy => Volatile.Read(ref _requests) > 0;

    internal bool TryJoin()
    {
        if (Interlocked.Increment(ref _requests) <= 9) return true;
        Interlocked.Decrement(ref _requests);
        return false;
    }

    internal void Leave() => Interlocked.Decrement(ref _requests);
    public void Dispose() => Semaphore.Dispose();
}

public sealed class OllamaJournalService(HttpClient client, IOptions<OllamaOptions> options,
    LocalInferenceGate gate, ILogger<OllamaJournalService> logger)
    : JournalAiService(new OllamaModelClient(client, options, gate, logger), logger);

public sealed class OllamaModelClient(
    HttpClient client,
    IOptions<OllamaOptions> options,
    LocalInferenceGate gate,
    ILogger logger) : IJournalModelClient
{
    public bool IsConfigured => options.Value.Enabled;
    public string ProviderName => "IA local";
    public bool UsesExternalProvider => false;

    private const string InvalidReply =
        "La IA local devolvió una respuesta incompleta. Tu entrada está guardada y podés reintentar.";

    public async Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema,
        IReadOnlyList<JournalImage> images, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        // Also enforce this boundary if the service is constructed outside dependency injection.
        if (!settings.IsValid())
            throw new JournalReflectionException("La configuración de la IA local no es válida. Tu entrada quedó guardada.");
        if (!gate.TryJoin())
            throw new JournalReflectionException("La IA local está atendiendo otras entradas. La tuya está guardada; probá de nuevo en un momento.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var acquired = false;
        try
        {
            await gate.Semaphore.WaitAsync(timeout.Token);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), "/api/chat"));
            request.Content = JsonContent.Create(new
            {
                model = settings.Model,
                messages = new object[]
                {
                    new { role = "system", content = instruction },
                    new { role = "user", content = prompt, images = images.Select(image => image.Base64).ToArray() }
                },
                format = schema,
                think = false,
                stream = false,
                keep_alive = "5m",
                options = new
                {
                    num_ctx = settings.ContextLength,
                    num_predict = settings.MaxOutputTokens,
                    temperature = 0.3
                }
            });
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("La IA local devolvió HTTP {StatusCode}.", (int)response.StatusCode);
                throw new JournalReflectionException(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "Falta instalar el modelo de la IA local. Tu entrada está guardada; avisá a la administración.",
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests =>
                        "La IA local está ocupada. Tu entrada está guardada y podés reintentar.",
                    HttpStatusCode.BadRequest => "La IA local no pudo analizar esta entrada o una de sus imágenes. El original quedó guardado.",
                    _ => "La IA local no está disponible en este momento. Tu entrada está guardada y podés reintentar."
                });
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("done_reason", out var reason) || reason.ValueKind != JsonValueKind.String || reason.GetString() != "stop" ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()))
                throw new JournalReflectionException(InvalidReply);

            logger.LogInformation("Generación local completada: entrada {InputTokens} tokens, salida {OutputTokens} tokens; carga {LoadMs} ms, lectura {PromptMs} ms, generación {GenerationMs} ms.",
                Metric(root, "prompt_eval_count"), Metric(root, "eval_count"), Metric(root, "load_duration") / 1_000_000,
                Metric(root, "prompt_eval_duration") / 1_000_000, Metric(root, "eval_duration") / 1_000_000);
            return content.GetString()!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JournalReflectionException("La IA local tardó más de lo previsto. Tu entrada está guardada y podés reintentar.");
        }
        catch (HttpRequestException)
        {
            throw new JournalReflectionException("No pudimos conectar con la IA local. Tu entrada quedó guardada; avisá a la administración.");
        }
        catch (JsonException)
        {
            throw new JournalReflectionException(InvalidReply);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            gate.Leave();
        }
    }

    private static long Metric(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
