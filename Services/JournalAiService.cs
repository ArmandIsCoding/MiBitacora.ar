using System.Text.Json;

namespace MiBitacora.Services;

public sealed record JournalImage(string Base64, string ContentType);

public interface IJournalModelClient
{
    bool IsConfigured { get; }
    string ProviderName { get; }
    bool UsesExternalProvider { get; }
    bool HasPremiumAccess => UsesExternalProvider;
    Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema,
        IReadOnlyList<JournalImage> images, CancellationToken cancellationToken);
}

// Evidence selection, prompts, source validation and persisted results are shared by both providers.
public abstract partial class JournalAiService(IJournalModelClient model, ILogger logger)
{
    public bool IsConfigured => model.IsConfigured;
    public string ProviderName => model.ProviderName;
    public bool UsesExternalProvider => model.UsesExternalProvider;
    public bool HasPremiumAccess => model.HasPremiumAccess;
    private const string InvalidReply =
        "La respuesta de Brisa quedó incompleta. Tu entrada está guardada y podés reintentar.";

    private Task<string> CallModelAsync(JournalEvidence evidence, CancellationToken cancellationToken) =>
        CallModelAsync(SystemInstruction + "\n\n" + ConversationVoiceInstruction +
            (UsesExternalProvider ? "" : "\nSi el mensaje lo requiere, desarrollá la idea principal en 120 a 220 palabras. Un saludo o pregunta sencilla merece pocas frases. La respuesta directa de Brisa va en message, no en themes. Dejá vacíos los campos opcionales que no aporten."),
            evidence.Prompt,
            ReflectionSchema, evidence.Images, cancellationToken);

    private Task<string> CallModelAsync(string instruction, string prompt, JsonElement schema,
        IReadOnlyList<JournalImage> images, CancellationToken cancellationToken) =>
        model.GenerateAsync(instruction, prompt, schema, images, cancellationToken);
}
