using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class JournalAiOptions
{
    public string Provider { get; set; } = "Ollama";
}

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Model { get; set; } = "gpt-6.1-terra";
    public string ReasoningEffort { get; set; } = "low";
    public int MaxOutputTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 180;

    public bool IsValid() => !string.IsNullOrWhiteSpace(ApiKey) &&
        !ApiKey.Any(char.IsWhiteSpace) && ProjectId.StartsWith("proj_", StringComparison.Ordinal) &&
        ProjectId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
        !string.IsNullOrWhiteSpace(Model) && Model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') &&
        ReasoningEffort is "none" or "low" or "medium" or "high" &&
        MaxOutputTokens is >= 2048 and <= 16384 && TimeoutSeconds is >= 30 and <= 600;
}

public sealed class OpenAiJournalService(HttpClient client, IOptions<OpenAiOptions> options,
    LocalInferenceGate gate, ILogger<OpenAiJournalService> logger)
    : JournalAiService(new OpenAiModelClient(client, options, gate, logger), logger);

public sealed class OpenAiModelClient(HttpClient client, IOptions<OpenAiOptions> options,
    LocalInferenceGate gate, ILogger logger) : IJournalModelClient
{
    // An incomplete cloud configuration must fail explicitly, never return a demo or switch provider.
    public bool IsConfigured => true;
    public string ProviderName => "OpenAI";
    public bool UsesExternalProvider => true;
    private const string InvalidReply = "OpenAI devolvió una respuesta incompleta. Podés reintentar; tus entradas siguen guardadas.";

    public async Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema,
        IReadOnlyList<JournalImage> images, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (!settings.IsValid())
            throw new JournalReflectionException("Falta configurar la conexión con OpenAI. Tus entradas siguen guardadas; avisá a la administración.");
        if (!gate.TryJoin())
            throw new JournalReflectionException("Hay varias devoluciones en curso. Probá de nuevo en un momento.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var acquired = false;
        try
        {
            await gate.Semaphore.WaitAsync(timeout.Token);
            acquired = true;
            var content = new List<object> { new { type = "input_text", text = prompt } };
            foreach (var image in images)
            {
                if (image.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
                    throw new JournalReflectionException("Una imagen no tiene un formato compatible. El original sigue guardado.");
                content.Add(new { type = "input_image", image_url = $"data:{image.ContentType};base64,{image.Base64}", detail = "auto" });
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Headers.Add("OpenAI-Project", settings.ProjectId);
            request.Content = JsonContent.Create(new
            {
                model = settings.Model,
                instructions = instruction,
                input = new[] { new { role = "user", content } },
                store = false,
                stream = false,
                max_output_tokens = settings.MaxOutputTokens,
                reasoning = new { effort = settings.ReasoningEffort },
                text = new { format = new { type = "json_schema", name = "journal_reflection", strict = true, schema = StrictSchema(schema) } }
            });
            // No automatic retry: a lost response may already have consumed credits.
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Never log provider error bodies: they can include journal content or secrets.
                logger.LogWarning("OpenAI devolvió HTTP {StatusCode}.", (int)response.StatusCode);
                throw new JournalReflectionException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "OpenAI no autorizó la conexión. Revisá la clave y los permisos del proyecto; tus entradas siguen guardadas.",
                    HttpStatusCode.TooManyRequests => "OpenAI alcanzó un límite de uso o de saldo. Revisá los créditos y límites; la devolución anterior se conserva.",
                    HttpStatusCode.NotFound => "El modelo configurado no está disponible en OpenAI. Avisá a la administración.",
                    HttpStatusCode.BadRequest => "OpenAI no pudo procesar esta solicitud. Tus entradas siguen guardadas; avisá a la administración.",
                    _ => "OpenAI no está disponible en este momento. Podés reintentar; tus entradas siguen guardadas."
                });
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) || status.GetString() != "completed" ||
                !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                throw new JournalReflectionException(InvalidReply);
            var answer = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var kind) || kind.GetString() != "message" ||
                    !item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("type", out var type)) continue;
                    if (type.GetString() == "refusal")
                        throw new JournalReflectionException("OpenAI no pudo generar una devolución para este contenido. Tus entradas siguen guardadas.");
                    if (type.GetString() == "output_text" && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        answer.Append(text.GetString());
                }
            }
            if (string.IsNullOrWhiteSpace(answer.ToString())) throw new JournalReflectionException(InvalidReply);
            logger.LogInformation("OpenAI completó una devolución con el modelo {Model}.", settings.Model);
            return answer.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new JournalReflectionException("OpenAI tardó más de lo previsto. Podés reintentar; tus entradas siguen guardadas."); }
        catch (HttpRequestException)
        { throw new JournalReflectionException("No pudimos conectar con OpenAI. Podés reintentar; tus entradas siguen guardadas."); }
        catch (JsonException) { throw new JournalReflectionException(InvalidReply); }
        catch (InvalidOperationException) { throw new JournalReflectionException(InvalidReply); }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            gate.Leave();
        }
    }

    private static JsonNode StrictSchema(JsonElement source)
    {
        var schema = JsonNode.Parse(source.GetRawText())!;
        CloseObjects(schema);
        return schema;
        static void CloseObjects(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["type"]?.GetValue<string>() == "object") obj["additionalProperties"] = false;
                foreach (var child in obj.ToArray()) CloseObjects(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) CloseObjects(child);
        }
    }
}
