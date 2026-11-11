using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class MercadoPagoGateway(HttpClient client, IOptions<SubscriptionOptions> options,
    IWebHostEnvironment environment) : ISubscriptionGateway
{
    private const string Api = "https://api.mercadopago.com";
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        string? idempotencyKey = null)
    {
        var config = options.Value;
        if (config.Provider != "MercadoPago" || !config.AllowLiveRequests || !config.IsValid(environment.IsDevelopment()))
            throw new SubscriptionException("Los cobros reales no están habilitados en esta instalación.");
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);
        if (idempotencyKey is not null) request.Headers.Add("X-Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (method == HttpMethod.Post && path == "/preapproval" &&
                response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.UnprocessableEntity)
                throw new SubscriptionCreationRejectedException();
            throw new GatewayRequestException(response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 2_000_000) throw new SubscriptionException("La respuesta de Mercado Pago es demasiado extensa.");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    public async Task<RemoteSubscription> CreateAsync(string reference, string payerEmail, long amountCents, CancellationToken ct) =>
        ReadSubscription(await SendAsync(HttpMethod.Post, "/preapproval", new
        {
            reason = "Mi Bitácora · Plan Profundo",
            external_reference = reference,
            payer_email = payerEmail,
            auto_recurring = new { frequency = 1, frequency_type = "months", transaction_amount = amountCents / 100m, currency_id = "ARS" },
            back_url = options.Value.PublicBaseUrl.TrimEnd('/') + "/Account/Manage/Subscription?returned=1",
            status = "pending"
        }, ct, reference));

    public async Task<RemoteSubscription> GetAsync(string id, CancellationToken ct) =>
        ReadSubscription(await SendAsync(HttpMethod.Get, "/preapproval/" + Escape(id), null, ct));

    public async Task<RemoteSubscription?> FindAsync(string reference, CancellationToken ct)
    {
        RemoteSubscription? found = null;
        // Recovery only. Do not rely on an undocumented external_reference search filter: compare every returned reference.
        for (var offset = 0; offset < 10_000;)
        {
            var json = await SendAsync(HttpMethod.Get, $"/preapproval/search?limit=100&offset={offset}", null, ct);
            var rows = json.GetProperty("results").EnumerateArray().ToArray();
            foreach (var match in rows.Where(r => Text(r, "external_reference") == reference))
            {
                if (found is not null) throw new SubscriptionException("Hay más de una suscripción para esta contratación. Requiere revisión.");
                found = ReadSubscription(match);
            }
            offset += rows.Length;
            var total = json.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var value)
                ? value.GetInt32() : (int?)null;
            if (total.HasValue && offset >= total.Value) return found;
            if (rows.Length == 0)
            {
                if (total.HasValue) throw new SubscriptionException("No pudimos completar la búsqueda de la contratación.");
                return found;
            }
        }
        throw new SubscriptionException("La búsqueda de la contratación requiere revisión. No se iniciará un segundo cobro.");
    }

    public async Task<RemoteInvoice> GetInvoiceAsync(string id, CancellationToken ct)
    {
        var row = await SendAsync(HttpMethod.Get, "/authorized_payments/" + Escape(id), null, ct);
        var paymentId = row.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object &&
            payment.TryGetProperty("id", out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
        return new(Text(row, "id"), Text(row, "preapproval_id"), paymentId, Date(row, "debit_date"));
    }

    public async Task<IReadOnlyList<RemoteInvoice>> GetInvoicesAsync(string subscriptionId, CancellationToken ct)
    {
        var invoices = new List<RemoteInvoice>();
        for (var offset = 0; offset < 10_000;)
        {
            var json = await SendAsync(HttpMethod.Get,
                $"/authorized_payments/search?preapproval_id={Escape(subscriptionId)}&limit=100&offset={offset}", null, ct);
            var rows = json.GetProperty("results").EnumerateArray().ToArray();
            foreach (var row in rows)
            {
                string? paymentId = row.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object &&
                    payment.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null ? id.ToString() : null;
                invoices.Add(new(Text(row, "id"), Text(row, "preapproval_id"), paymentId, Date(row, "debit_date")));
            }
            offset += rows.Length;
            // Mercado Pago may clamp the requested limit. Use the actual page size and reported total.
            var total = json.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var value)
                ? value.GetInt32() : (int?)null;
            if (total.HasValue && offset >= total.Value) return invoices;
            if (rows.Length == 0)
            {
                if (total.HasValue) throw new SubscriptionException("No pudimos recuperar todas las cuotas. Se reintentará la conciliación.");
                return invoices;
            }
        }
        throw new SubscriptionException("La conciliación requiere revisar demasiadas cuotas. El acceso existente se conserva.");
    }

    public async Task<RemotePayment> GetPaymentAsync(string id, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/v1/payments/" + Escape(id), null, ct);
        return new(Text(json, "id"), Text(json, "collector_id"), Text(json, "external_reference"), Text(json, "status"),
            Money(json, "transaction_amount"), Text(json, "currency_id"), Date(json, "date_last_updated"));
    }

    public async Task CancelAsync(string id, CancellationToken ct)
    {
        JsonElement json;
        try { json = await SendAsync(HttpMethod.Put, "/preapproval/" + Escape(id), new { status = "canceled" }, ct); }
        catch (GatewayRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Current documentation uses canceled; SDKs and some API versions use cancelled.
            json = await SendAsync(HttpMethod.Put, "/preapproval/" + Escape(id), new { status = "cancelled" }, ct);
        }
        if (Text(json, "id") != id || Text(json, "status") is not ("canceled" or "cancelled"))
            throw new SubscriptionException("Mercado Pago todavía no confirmó la cancelación. El pedido queda guardado para reintentarlo.");
    }

    private sealed class GatewayRequestException(System.Net.HttpStatusCode statusCode)
        : SubscriptionException("Mercado Pago no pudo completar la operación. Tu cuaderno y tu acceso ya abonado se conservan.")
    { public System.Net.HttpStatusCode StatusCode => statusCode; }

    private static RemoteSubscription ReadSubscription(JsonElement json)
    {
        var recurring = json.GetProperty("auto_recurring");
        return new(Text(json, "id"), Text(json, "external_reference"), Text(json, "collector_id"), Text(json, "status"),
            Money(recurring, "transaction_amount"), Text(recurring, "currency_id"), recurring.GetProperty("frequency").GetInt32(),
            Text(recurring, "frequency_type"), Date(json, "last_modified"),
            json.TryGetProperty("init_point", out var checkout) ? checkout.GetString() : null);
    }
    private static string Escape(string id) => Uri.EscapeDataString(id);
    private static string Text(JsonElement json, string name) => json.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    private static DateTime Date(JsonElement json, string name) => DateTimeOffset.Parse(Text(json, name), CultureInfo.InvariantCulture).UtcDateTime;
    private static long Money(JsonElement json, string name)
    {
        var value = decimal.Parse(Text(json, name), CultureInfo.InvariantCulture);
        if (value < 0 || decimal.Round(value, 2) != value) throw new SubscriptionException("El importe recibido no es válido.");
        return checked((long)(value * 100));
    }
}
