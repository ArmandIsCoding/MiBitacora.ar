using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class OllamaAudioTranscriber(HttpClient client, IOptions<OllamaOptions> options,
    LocalInferenceGate gate) : IAudioTranscriber
{
    public bool IsConfigured => options.Value.Enabled && options.Value.Model == "gemma4:12b" && options.Value.IsValid();
    public async Task<string> TranscribeAsync(AttachmentContent file, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new JournalReflectionException("La transcripción local requiere Gemma 4 12B. Tu audio sigue guardado.");
        // Ollama currently accepts WAV at this endpoint; free audio must never fall back to OpenAI.
        if (file.ContentType != "audio/wav")
            throw new JournalReflectionException("La transcripción local necesita audio WAV. El original sigue guardado: podés adjuntar un WAV o escribir lo que querés contar.");
        if (!file.Content.CanSeek || file.Content.Length is <= 0 or > JournalStore.MaxFileBytes)
            throw new JournalReflectionException("No pudimos leer el audio o supera los 8 MB permitidos.");
        if (!gate.TryJoin()) throw new JournalReflectionException("La IA local está ocupada. El audio sigue guardado; podés reintentar.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var acquired = false;
        try
        {
            await gate.Semaphore.WaitAsync(timeout.Token);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Value.BaseUrl), "/v1/audio/transcriptions"));
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(options.Value.Model), "model");
            form.Add(new StringContent("es"), "language");
            form.Add(new StringContent("json"), "response_format");
            file.Content.Position = 0;
            var audio = new StreamContent(file.Content);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "audio.wav");
            request.Content = form;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new JournalReflectionException("Ollama no pudo transcribir el audio. Verificá que la versión instalada y Gemma admitan audio; el original sigue guardado.");
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + count > 1_000_000) throw new JournalReflectionException("La transcripción local es demasiado extensa.");
                buffer.Write(bytes, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var text = json.RootElement.GetProperty("text").GetString()?.Trim();
            if (text is null || text.Length is 0 or > 120_000) throw new JournalReflectionException("No se obtuvo una transcripción local legible.");
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new JournalReflectionException("La transcripción local tardó más de lo previsto. El audio sigue guardado."); }
        catch (HttpRequestException) { throw new JournalReflectionException("No se pudo conectar con la transcripción local. Tu audio sigue guardado."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new JournalReflectionException("La transcripción local llegó incompleta. Tu audio sigue guardado."); }
        finally { if (acquired) gate.Semaphore.Release(); gate.Leave(); }
    }
}
