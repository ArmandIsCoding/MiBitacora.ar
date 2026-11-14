using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class SubscriptionOptions
{
    // Live requests require two explicit settings. No production credentials are shipped.
    public string Provider { get; set; } = "Disabled";
    public bool AllowLiveRequests { get; set; }
    public string AccessToken { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string CollectorId { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "http://localhost:5234";
    public decimal MonthlyPriceArs { get; set; }
    public int ReconcileMinutes { get; set; } = 15;
    public long PriceCents => checked((long)(MonthlyPriceArs * 100));
    public bool IsValid(bool development)
    {
        if (Provider == "Disabled") return true;
        if (ReconcileMinutes is < 1 or > 1440 || MonthlyPriceArs is <= 0 or > 10_000_000 ||
            decimal.Round(MonthlyPriceArs, 2) != MonthlyPriceArs ||
            !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var url) || url.AbsolutePath != "/" ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) return false;
        if (Provider == "Simulated") return development && url.IsLoopback && !AllowLiveRequests;
        return Provider == "MercadoPago" && AllowLiveRequests && url.Scheme == "https" &&
            !string.IsNullOrWhiteSpace(AccessToken) && !AccessToken.Any(char.IsWhiteSpace) &&
            WebhookSecret.Length >= 16 && CollectorId.Length > 0 && CollectorId.All(char.IsAsciiDigit);
    }
}

public sealed record SubscriptionAccess(bool IsPaid, DateTime? PaidThroughUtc, bool IsComplimentary = false,
    string AiProvider = "OpenAI", bool OpenAiForAllAccounts = false)
{
    public static readonly SubscriptionAccess Free = new(false, null);
    public bool CanUseOpenAi => IsPaid || OpenAiForAllAccounts;
}
public sealed record SubscriptionSummary(Guid Id, string Status, bool CancellationRequested, long MonthlyPriceCents,
    DateTime? PaidThroughUtc, string? CheckoutUrl, string Provider);
public sealed record SubscriptionPayment(string Id, string Status, long AmountCents, DateTime FromUtc, DateTime ThroughUtc);
public class SubscriptionException(string message) : Exception(message);
// A definitive rejection of POST /preapproval is safe to retry as a new intent. Transport errors are ambiguous.
public sealed class SubscriptionCreationRejectedException() : SubscriptionException("Mercado Pago rechazó la contratación. Podés volver a intentarlo desde Mi plan.");

public sealed record RemoteSubscription(string Id, string ExternalReference, string CollectorId, string Status,
    long AmountCents, string Currency, int Frequency, string FrequencyType, DateTime UpdatedUtc, string? CheckoutUrl);
public sealed record RemoteInvoice(string Id, string SubscriptionId, string? PaymentId, DateTime DebitUtc);
public sealed record RemotePayment(string Id, string CollectorId, string ExternalReference, string Status,
    long AmountCents, string Currency, DateTime UpdatedUtc);

public interface ISubscriptionGateway
{
    Task<RemoteSubscription> CreateAsync(string reference, string payerEmail, long amountCents, CancellationToken ct);
    Task<RemoteSubscription> GetAsync(string id, CancellationToken ct);
    Task<RemoteSubscription?> FindAsync(string reference, CancellationToken ct);
    Task<RemoteInvoice> GetInvoiceAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<RemoteInvoice>> GetInvoicesAsync(string subscriptionId, CancellationToken ct);
    Task<RemotePayment> GetPaymentAsync(string id, CancellationToken ct);
    Task CancelAsync(string id, CancellationToken ct);
}
