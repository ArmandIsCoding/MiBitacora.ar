using System.Text.Json;

namespace MiBitacora.Services;

public sealed record AccountAiSelection(string UserId, SubscriptionAccess Access);

// Enter synchronously after resolving access: AsyncLocal assignments inside an async callee do not propagate to its caller.
public sealed class AccountAiRouter(SubscriptionService subscriptions, OllamaModelClient local,
    OpenAiModelClient cloud) : IJournalModelClient
{
    private readonly AsyncLocal<AccountAiSelection?> _operation = new();
    private SubscriptionAccess _display = SubscriptionAccess.Free;
    public bool HasPremiumAccess => (_operation.Value?.Access ?? _display).IsPaid;
    public bool UsesExternalProvider => HasPremiumAccess && (_operation.Value?.Access ?? _display).AiProvider != "Ollama";
    public string ProviderName => UsesExternalProvider ? "Plan Profundo · OpenAI" : HasPremiumAccess ? "Plan Profundo · IA local" : "Plan gratuito · IA local";
    public bool IsConfigured => UsesExternalProvider || local.IsConfigured;
    public async Task<AccountAiSelection> ResolveAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new JournalInputException("Volvé a ingresar para usar tu bitácora.");
        var access = await subscriptions.GetAccessAsync(userId, ct);
        _display = access;
        return new(userId, access);
    }
    public IDisposable Use(AccountAiSelection selection)
    {
        if (_operation.Value is { } current && current.UserId != selection.UserId)
            throw new InvalidOperationException("No se pueden mezclar cuentas en una operación de IA.");
        var previous = _operation.Value;
        _operation.Value = previous ?? selection;
        return new Restore(() => _operation.Value = previous);
    }
    public async Task EnsureAccessAsync(CancellationToken ct)
    {
        var operation = _operation.Value ?? throw new JournalReflectionException("Falta identificar tu cuenta para preparar la devolución.");
        if (UsesExternalProvider && !(await subscriptions.GetAccessAsync(operation.UserId, ct)).IsPaid)
            throw new JournalReflectionException("Tu período pago terminó durante la preparación. Tu cuaderno se conserva; volvé a intentar con tu plan actual.");
    }
    public async Task<string> GenerateAsync(string instruction, string prompt, JsonElement schema,
        IReadOnlyList<JournalImage> images, CancellationToken cancellationToken)
    {
        await EnsureAccessAsync(cancellationToken);
        return await (UsesExternalProvider ? (IJournalModelClient)cloud : local)
            .GenerateAsync(instruction, prompt, schema, images, cancellationToken);
    }
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}

public sealed class AccountJournalService(AccountAiRouter router, ILogger<AccountJournalService> logger)
    : JournalAiService(router, logger)
{
    public Task<AccountAiSelection> SelectAccountAsync(string userId, CancellationToken ct = default) => router.ResolveAsync(userId, ct);
}

public sealed class AccountAudioTranscriber(AccountAiRouter routing, OpenAiAudioTranscriber cloud,
    OllamaAudioTranscriber local) : IAudioTranscriber
{
    public bool IsConfigured => routing.UsesExternalProvider ? cloud.IsConfigured : local.IsConfigured;
    public async Task<string> TranscribeAsync(AttachmentContent file, CancellationToken ct = default)
    {
        await routing.EnsureAccessAsync(ct);
        return await (routing.UsesExternalProvider ? (IAudioTranscriber)cloud : local).TranscribeAsync(file, ct);
    }
}
