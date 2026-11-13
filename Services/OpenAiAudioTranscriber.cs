using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class AudioTranscriptionOptions
{
    public string Model { get; set; } = "gpt-transcribe";
    public int TimeoutSeconds { get; set; } = 180;
    public bool IsValid() => Model == "gpt-transcribe" && TimeoutSeconds is >= 30 and <= 600;
}
public interface IAudioTranscriber
{
    bool IsConfigured { get; }
    Task<string> TranscribeAsync(AttachmentContent file, CancellationToken ct = default);
}

public sealed class OpenAiAudioTranscriber(HttpClient client, IOptions<OpenAiOptions> credentials,
    IOptions<JournalAiOptions> provider, IOptions<AudioTranscriptionOptions> options,
    LocalInferenceGate gate, ILogger<OpenAiAudioTranscriber> logger) : IAudioTranscriber
{
    public bool IsConfigured => provider.Value.Provider == "OpenAI" && credentials.Value.IsValid() && options.Value.IsValid();
    public async Task<string> TranscribeAsync(AttachmentContent file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new JournalReflectionException("La transcripción requiere OpenAI configurado. El audio queda guardado sin enviarse a otro proveedor.");
        var extension = file.ContentType switch
        {
            "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/wav" => ".wav",
            "audio/webm" => ".webm", "audio/ogg" => ".ogg", "audio/flac" => ".flac", _ => null
        };
        if (extension is null) throw new JournalReflectionException("Este formato se puede guardar, pero no transcribir. Probá con M4A, MP3, WAV, OGG, FLAC o WebM.");
        if (!file.Content.CanSeek || file.Content.Length is <= 0 or > AttachmentPreparationService.MaxAudioBytes)
            throw new JournalReflectionException("El audio supera los 20 MB permitidos para transcribir o no se pudo leer. Volvé a adjuntarlo para optimizarlo.");
        if (!gate.TryJoin()) throw new JournalReflectionException("Hay varias solicitudes en curso. Tu audio sigue guardado; probá de nuevo en un momento.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var acquired = false;
        try
        {
            await gate.Semaphore.WaitAsync(timeout.Token); acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Value.ApiKey);
            request.Headers.Add("OpenAI-Project", credentials.Value.ProjectId);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(options.Value.Model), "model");
            file.Content.Position = 0;
            var audio = new StreamContent(file.Content);
            audio.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            form.Add(audio, "file", "audio" + extension);
            request.Content = form;
            // No journal/profile hints: the transcript must follow the recording, not suggest names or events.
            // No automatic retries: a request may already have consumed credits.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OpenAI transcripción devolvió HTTP {Status}.", (int)response.StatusCode);
                throw new JournalReflectionException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "OpenAI no autorizó la transcripción. Revisá el permiso de audio de la clave; el original sigue guardado.",
                    HttpStatusCode.TooManyRequests => "Se alcanzó un límite de uso o de saldo al transcribir. El audio sigue guardado para reintentar.",
                    HttpStatusCode.BadRequest => "OpenAI no pudo leer este audio. El original sigue disponible; probá con otro formato.",
                    _ => "No se pudo transcribir el audio en este momento. Podés reintentar sin volver a subirlo."
                });
            }
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192]; int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 1024 * 1024) throw new JournalReflectionException("La transcripción recibida es demasiado extensa. El original se conserva.");
                buffer.Write(chunk, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (!json.RootElement.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
                throw new JournalReflectionException("OpenAI no devolvió una transcripción válida. El audio sigue guardado.");
            var transcript = value.GetString()!.Trim();
            if (transcript.Length is 0 or > 120_000) throw new JournalReflectionException("No se obtuvo una transcripción legible. Escuchá el audio y volvé a intentarlo.");
            return transcript;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new JournalReflectionException("La transcripción tardó más de lo previsto. El audio sigue guardado para reintentar."); }
        catch (HttpRequestException) { throw new JournalReflectionException("No pudimos conectar para transcribir. El audio sigue guardado."); }
        catch (JsonException) { throw new JournalReflectionException("La transcripción recibida no es válida. El original se conserva."); }
        finally { if (acquired) gate.Semaphore.Release(); gate.Leave(); }
    }
}
