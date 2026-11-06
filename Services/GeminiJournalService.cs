using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MiBitacora.Services;

public sealed partial class GeminiJournalService(
    HttpClient client,
    IConfiguration configuration,
    ILogger<GeminiJournalService> logger)
{
    public const string DefaultModel = "gemini-3.5-flash-lite";

    private string? ApiKey
    {
        get
        {
            var configured = configuration["Gemini:ApiKey"];
            return string.IsNullOrWhiteSpace(configured) ? configuration["GEMINI_API_KEY"] : configured;
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    private string Model =>
        string.IsNullOrWhiteSpace(configuration["Gemini:Model"])
            ? DefaultModel
            : configuration["Gemini:Model"]!.Trim();

    private async Task<GeminiApiReply> CallGeminiAsync(
        IReadOnlyList<object> parts,
        object schema,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(65));

        var model = Model;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
        request.Headers.Add("x-goog-api-key", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            store = false,
            systemInstruction = new { parts = new[] { new { text = SystemInstruction } } },
            contents = new[] { new { role = "user", parts } },
            generationConfig = new
            {
                candidateCount = 1,
                maxOutputTokens = 2300,
                responseMimeType = "application/json",
                responseJsonSchema = schema
            }
        });

        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini devolvió el estado HTTP {StatusCode}.", (int)response.StatusCode);
                return new(null, DescribeFailure(response.StatusCode));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return ParseReply(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Se agotó el tiempo de espera de Gemini.");
            return new(null, "Gemini tardó más de lo previsto. Tu entrada está guardada y podés reintentar.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("No se pudo completar la conexión a Gemini.");
            return new(null, "No pudimos conectar con Gemini. Tu entrada está guardada y podés reintentar.");
        }
        catch (JsonException)
        {
            logger.LogWarning("Gemini devolvió una respuesta HTTP sin JSON válido.");
            return new(null, InvalidReply);
        }
    }

    private const string InvalidReply =
        "Gemini devolvió una respuesta incompleta. Tu entrada está guardada y podés reintentar.";

    private GeminiApiReply ParseReply(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new(null, InvalidReply);
        }

        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.ValueKind == JsonValueKind.Object &&
            feedback.TryGetProperty("blockReason", out var reason) &&
            reason.ValueKind == JsonValueKind.String &&
            reason.GetString() is not "BLOCK_REASON_UNSPECIFIED")
        {
            return new(null, FilteredReply);
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return new(null, InvalidReply);
        }

        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return new(null, InvalidReply);
        }

        var finishReason = candidate.TryGetProperty("finishReason", out var finish) &&
            finish.ValueKind == JsonValueKind.String ? finish.GetString() : null;
        if (finishReason is not "STOP")
        {
            return new(null, finishReason is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII"
                ? FilteredReply
                : InvalidReply);
        }

        if (!candidate.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return new(null, InvalidReply);
        }

        var answer = string.Concat(parts.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object &&
                (!part.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True))
            .Select(part => part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null));

        if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            logger.LogInformation(
                "Gemini completó una organización. Tokens de entrada: {PromptTokens}. Tokens totales: {TotalTokens}.",
                ReadCount(usage, "promptTokenCount"),
                ReadCount(usage, "totalTokenCount"));
        }

        return string.IsNullOrWhiteSpace(answer) ? new(null, InvalidReply) : new(answer, null);
    }

    private static int? ReadCount(JsonElement usage, string key) =>
        usage.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var count) ? count : null;

    private const string FilteredReply =
        "Gemini no pudo organizar esta entrada por sus filtros de contenido. Podés volver a leer el original guardado.";

    private static string DescribeFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests =>
            "El piloto alcanzó la cuota de Gemini. Tu entrada está guardada. Probá de nuevo más tarde.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "Gemini rechazó el acceso del piloto. Tu entrada quedó guardada. Revisá la configuración de la clave.",
        HttpStatusCode.NotFound =>
            "El modelo configurado no está disponible para este proyecto de Gemini. Tu entrada quedó guardada.",
        HttpStatusCode.RequestEntityTooLarge =>
            "Gemini no pudo recibir los adjuntos por su tamaño. Tu entrada y sus archivos quedaron guardados.",
        HttpStatusCode.BadRequest =>
            "Gemini no pudo procesar esta entrada o uno de sus adjuntos. El original quedó guardado.",
        _ => "Gemini no está disponible en este momento. Tu entrada está guardada y podés reintentar."
    };

    private sealed record GeminiApiReply(string? Json, string? Error);
}
