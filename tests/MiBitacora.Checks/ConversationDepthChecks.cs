using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MiBitacora.Services;

internal static class ConversationDepthChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var client = new CapturedModel();
        var service = new ReflectionService(client);
        var prior = new JournalEntry(Guid.NewGuid(), "Una entrada anterior.", DateTime.UtcNow.AddMinutes(-1),
            new("Lectura", client.Reply, [], ["¿Qué necesitás cuidar?"], [], null, false, 1), false, []);
        var context = new JournalContext(new("Prueba", "", false),
            new(Guid.NewGuid(), "Necesito tiempo para mí.", DateTime.UtcNow, null, false, []), [prior], [], [], []);
        var reflection = await service.ReflectAsync(context, true);
        check(reflection.Overview == client.Reply && reflection.Overview.Length > 1800,
            "Profundidad: una lectura desarrollada se conserva completa después del antiguo corte");
        var sources = JsonDocument.Parse(client.Prompt[client.Prompt.IndexOf('{')..]).RootElement.GetProperty("fuentes");
        check(sources.EnumerateArray().Any(s => s.GetProperty("devolucionAnterior").GetString()?.Contains("CIERRE_DE_LA_LECTURA_ANTERIOR") == true),
            "Profundidad: el siguiente turno conserva la conclusión de una respuesta extensa anterior");
        check(reflection.OpenQuestions.Count == 1 && reflection.Themes.Count == 0,
            "Profundidad: conserva una pregunta específica y permite una respuesta sin apoyos de relleno");
        check(reflection.OpenQuestions.Single() == "¿Qué te gustaría proteger de ese tiempo?",
            "Profundidad: no presenta restos de generación después de la pregunta completa");
        client.Question = "¿Qué te gustaría proteger de ese";
        var incomplete = await service.ReflectAsync(context, true);
        check(incomplete.OpenQuestions.Count == 0,
            "Profundidad: omite una pregunta incompleta en lugar de mostrar un fragmento");
    }
    private sealed class ReflectionService(IJournalModelClient model) : JournalAiService(model, NullLogger.Instance);
    private sealed class CapturedModel : IJournalModelClient
    {
        public string Reply { get; } = string.Concat(Enumerable.Repeat("Una lectura de prueba sostenida en el mensaje. ", 62)) + "CIERRE_DE_LA_LECTURA_ANTERIOR";
        public string Prompt { get; private set; } = "";
        public string Question { get; set; } = "¿Qué te gustaría proteger de ese tiempo? resto incompleto";
        public bool IsConfigured => true;
        public string ProviderName => "Prueba";
        public bool UsesExternalProvider => true;
        public Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema, IReadOnlyList<JournalImage> images, CancellationToken ct)
        {
            Prompt = prompt;
            return Task.FromResult(JsonSerializer.Serialize(new { title = "Un espacio propio", overview = Reply,
                themes = Array.Empty<object>(), openQuestions = new[] { Question },
                sessionTopics = Array.Empty<string>(), nextStep = "" }));
        }
    }
}
