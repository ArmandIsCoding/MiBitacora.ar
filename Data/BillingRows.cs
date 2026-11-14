namespace MiBitacora.Data;

// Administrative access, independent of payment and subscription records.
public sealed class BillingAccessGrantRow
{
    public string UserId { get; set; } = "";
    public DateTime GrantedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class AccountAiPreferenceRow
{
    public string UserId { get; set; } = "";
    public string Provider { get; set; } = "OpenAI";
}

public sealed class BillingSubscriptionRow
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ProviderId { get; set; }
    public string ExternalReference { get; set; } = "";
    public string Status { get; set; } = "creating";
    public long MonthlyPriceCents { get; set; }
    public string Currency { get; set; } = "ARS";
    public string? CheckoutUrl { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ProviderUpdatedUtc { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public DateTime? LastSyncAttemptUtc { get; set; }
    public DateTime? BillingAnchorUtc { get; set; }
    public bool CancellationRequested { get; set; }
}

public sealed class BillingPaymentRow
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public string ProviderPaymentId { get; set; } = "";
    public string ProviderInvoiceId { get; set; } = "";
    public string Status { get; set; } = "";
    public long AmountCents { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime ProviderUpdatedUtc { get; set; }
}

public sealed class BillingWebhookRow
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public int Attempts { get; set; }
}
