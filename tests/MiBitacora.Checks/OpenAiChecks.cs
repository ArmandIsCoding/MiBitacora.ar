using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiBitacora.Components.Pages;
using MiBitacora.Services;

internal static class OpenAiChecks
{
    internal static async Task RunAsync(Action<bool, string> check, JournalContext context, bool live)
    {
        using var gate = new LocalInferenceGate();
        var settings = new OpenAiOptions { ApiKey = "synthetic-test-key", ProjectId = "proj_test" };
        var answer = JsonSerializer.Serialize(new
        {
            title = "Apuntes", overview = "Registraste un momento para organizar tus apuntes.",
            themes = new[] { new { title = "Organización", observation = "Ordenaste tus apuntes.", interpretation = "", quote = "Inventado", sourceId = "actual" } },
            openQuestions = Array.Empty<string>(), sessionTopics = Array.Empty<string>(), nextStep = ""
        });
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            check(request.RequestUri == new Uri("https://api.openai.com/v1/responses"), "OpenAI: destino HTTPS fijo");
            check(request.Headers.Authorization?.Parameter == settings.ApiKey &&
                request.Headers.GetValues("OpenAI-Project").Single() == "proj_test", "OpenAI: autenticación y proyecto correctos");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            check(!body.GetProperty("store").GetBoolean(), "OpenAI: no persiste estado de la respuesta");
            check(!body.TryGetProperty("tools", out _) && !body.TryGetProperty("previous_response_id", out _), "OpenAI: sin herramientas ni conversaciones compartidas");
            var content = body.GetProperty("input")[0].GetProperty("content");
            check(content[1].GetProperty("image_url").GetString() == "data:image/png;base64," + Convert.ToBase64String(context.Media[0].Content), "OpenAI: imagen con MIME y contenido originales");
            check(body.GetProperty("max_output_tokens").GetInt32() == 4096, "OpenAI: generación con presupuesto acotado");
            AssertStrict(body.GetProperty("text").GetProperty("format").GetProperty("schema"));
            return Reply(answer);
        }));
        var model = Service(http, gate, settings);
        var result = await model.ReflectAsync(context, true);
        check(!result.IsDemo && result.Themes.Single().Quote is null, "OpenAI: reutiliza la validación de citas");
        var demo = await model.ReflectAsync(context, false);
        check(demo.IsDemo && calls == 1, "OpenAI: guardar o usar muestra no envía contenido");
        try { await Service(http, gate, new()).ReflectAsync(context, true); throw new Exception("Clave vacía aceptada"); }
        catch (JournalReflectionException) { check(calls == 1, "OpenAI: clave ausente falla sin enviar ni sustituir por muestra"); }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            var errorCalls = 0;
            using var failed = new HttpClient(new Handler((_, _) =>
            {
                errorCalls++;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("SECRET_PROVIDER_ERROR") });
            }));
            try { await Service(failed, gate, settings).ReflectAsync(context, true); throw new Exception("Error HTTP aceptado"); }
            catch (JournalReflectionException ex)
            { check(errorCalls == 1 && !ex.Message.Contains("SECRET_PROVIDER_ERROR"), $"OpenAI: HTTP {(int)status} sin reintentos ni filtración del cuerpo"); }
        }
        foreach (var state in new[] { "incomplete", "failed", "cancelled" })
        {
            using var truncated = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(answer, state))));
            try { await Service(truncated, gate, settings).ReflectAsync(context, true); throw new Exception("Respuesta incompleta aceptada"); }
            catch (JournalReflectionException) { check(true, "OpenAI: rechaza " + state); }
        }
        using var refusalHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "refusal", refusal = "PRIVATE_REFUSAL" } } } } })
        })));
        try { await Service(refusalHttp, gate, settings).ReflectAsync(context, true); throw new Exception("Refusal aceptado"); }
        catch (JournalReflectionException ex) { check(!ex.Message.Contains("PRIVATE_REFUSAL"), "OpenAI: rechazo sin mostrar contenido del proveedor"); }

        foreach (var malformed in new[] { "not-json", "{\"status\":42,\"output\":[]}", "{\"status\":\"completed\",\"output\":[]}" })
        {
            using var malformedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(malformed) })));
            try { await Service(malformedHttp, gate, settings).ReflectAsync(context, true); throw new Exception("Respuesta inválida aceptada"); }
            catch (JournalReflectionException) { check(true, "OpenAI: respuesta malformada produce un error recuperable"); }
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueCalls = 0;
        using var queuedHttp = new HttpClient(new Handler(async (_, ct) =>
        {
            if (++queueCalls == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); }
            return Reply(answer);
        }));
        var first = Service(queuedHttp, gate, settings).ReflectAsync(context, true);
        await entered.Task;
        using var canceled = new CancellationTokenSource();
        var second = Service(queuedHttp, gate, settings).ReflectAsync(context, true, canceled.Token);
        canceled.Cancel();
        try { await second; throw new Exception("Faltó cancelación"); } catch (OperationCanceledException) { }
        release.SetResult(); await first;
        await Service(queuedHttp, gate, settings).ReflectAsync(context, true);
        check(queueCalls == 2, "OpenAI: cancelación en cola no consume una solicitud y libera su lugar");

        var services = new ServiceCollection().AddLogging().AddSingleton<IJournalReflector>(model);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Privacidad>(ParameterView.Empty)).ToHtmlString());
        check(markup.Contains("OpenAI") && markup.Contains("fuera del servidor") && !markup.Contains("no se envía ese contenido"),
            "OpenAI: la página de privacidad informa procesamiento externo");

        if (live) await LiveAsync(context, gate, check);
    }

    private static void AssertStrict(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object" &&
                (!node.TryGetProperty("additionalProperties", out var extra) || extra.ValueKind != JsonValueKind.False))
                throw new Exception("Schema permite propiedades adicionales");
            foreach (var property in node.EnumerateObject()) AssertStrict(property.Value);
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) AssertStrict(child);
    }

    private static HttpResponseMessage Reply(string answer, string status = "completed") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { status, output = new object[]
        {
            new { type = "reasoning", summary = Array.Empty<string>() },
            new { type = "message", content = new[] { new { type = "output_text", text = answer } } }
        } })
    };

    private static OpenAiJournalService Service(HttpClient client, LocalInferenceGate gate, OpenAiOptions settings) =>
        new(client, Options.Create(settings), gate, NullLogger<OpenAiJournalService>.Instance);

    private static async Task LiveAsync(JournalContext context, LocalInferenceGate gate, Action<bool, string> check)
    {
        var path = Environment.GetEnvironmentVariable("MIBITACORA_AI_CONFIG")
            ?? throw new Exception("Indicá MIBITACORA_AI_CONFIG para la prueba sintética real.");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var settings = config.RootElement.GetProperty("OpenAI").Deserialize<OpenAiOptions>()!;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var model = Service(http, gate, settings);
        var text = await model.ReflectAsync(context with { Current = context.Current with { Attachments = [] }, Attachments = [], Media = [] }, true);
        check(!text.IsDemo && text.Title.Length > 0, "API real: devolución de texto sintético");
        var image = await model.ReflectAsync(context with { Current = context.Current with { Text = "Quiero registrar el color y la forma de esta imagen de prueba." } }, true);
        check(!image.IsDemo && JsonSerializer.Serialize(image).Contains("rojo", StringComparison.OrdinalIgnoreCase), "API real: reconoce la imagen sintética roja");
        var source = new TopicSource("s1", Guid.NewGuid(), DateTime.UtcNow, "Entrada sintética", "Hoy caminé por el parque y disfruté del aire libre.");
        var topics = await model.DetectAsync([source], new Dictionary<string, string>(), default);
        check(topics.Count > 0 && topics.All(t => t.Evidence.All(e => e.SourceId == "s1")), "API real: detección de temas con fuentes válidas");
        var reading = await model.ReadAsync(topics[0].NewTitle, topics[0].Observation, default);
        check(reading.Overview.Length > 0, "API real: devolución temática sintética");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
