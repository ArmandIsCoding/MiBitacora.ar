using System.Net.Http.Headers;
using System.Text.Json;

namespace MiBitacora.Services;

// Read-only launch check. It never opens the database or creates a subscription/payment.
public static class BillingReadiness
{
    public static async Task<int> RunAsync(IConfiguration configuration, bool checkMerchant, bool checkMail = false)
    {
        var billing = configuration.GetSection("Subscriptions").Get<SubscriptionOptions>() ?? new();
        var mail = configuration.GetSection("Mail").Get<MailOptions>() ?? new();
        var ai = configuration.GetSection("OpenAI").Get<OpenAiOptions>() ?? new();
        var ok = true;
        void Check(bool valid, string message) { Console.WriteLine((valid ? "OK: " : "PENDIENTE: ") + message); ok &= valid; }
        Check(billing.Provider == "MercadoPago" && billing.IsValid(false), "Credenciales, vendedor, URL HTTPS y parámetros de Mercado Pago.");
        Console.WriteLine(billing.WebhookSecret.Length >= 16 ? "Notificaciones firmadas configuradas: verificar entrega real." : "AVISO: sin clave de firma; webhooks deshabilitados. Se usa conciliación periódica y actualización manual. Confirmá con Mercado Pago la modalidad de notificaciones para tu integración.");
        Check(billing.MonthlyPriceArs == 15_000, "Precio de lanzamiento: ARS 15.000 por mes.");
        Check(billing.MonthlyCredits >= 10 && billing.ReportCredits <= billing.MonthlyCredits, "Cupo mensual y costo de informes válidos.");
        Check(mail.Delivery == "Smtp" && mail.IsValid(false), "Correo SMTP con TLS configurado.");
        Check(ai.IsValid(), "Configuración de OpenAI completa.");
        Check(!configuration.GetValue<bool>("AI:OpenAiForAllAccounts"), "Acceso a OpenAI sujeto al plan en producción.");
        Check(configuration.GetSection("Administration:UserIds").Get<string[]>()?.Any(id => !string.IsNullOrWhiteSpace(id)) == true,
            "Al menos una cuenta administradora configurada.");
        Console.WriteLine("Ventas nuevas: " + (billing.AcceptNewSubscriptions ? "habilitadas por configuración" : "en pausa; la gestión de cobros sigue disponible"));
        if (billing.LaunchUserIds.Length > 0) Console.WriteLine("Contratación restringida a las cuentas de lanzamiento configuradas.");
        if (checkMerchant)
        {
            if (billing.Provider != "MercadoPago" || !billing.IsValid(false)) Check(false, "No se consultó al vendedor: faltan credenciales válidas.");
            else
            {
                try {
                    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mercadopago.com/users/me");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", billing.AccessToken);
                    using var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode) Check(false, "Mercado Pago no validó el acceso al vendedor.");
                    else {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        var root = document.RootElement;
                        Check(root.GetProperty("id").ToString() == billing.CollectorId && root.GetProperty("site_id").GetString() == "MLA", "El token pertenece al vendedor configurado de Argentina.");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { Check(false, "No se pudo verificar el vendedor. No se realizaron cobros."); }
            }
        }
        if (checkMail)
        {
            try {
                await SmtpDelivery.CheckConnectionAsync(mail);
                Check(true, "Conexión SMTP cifrada y autenticación verificadas. No se envió ningún mensaje.");
            }
            catch (Exception) { Check(false, "No se pudo verificar TLS y autenticación SMTP. Revisá servidor, puerto, usuario y clave en la configuración privada."); }
        }
        Console.WriteLine("Este control no valida entrega SMTP, disponibilidad de modelos, checkout, renovación, firma de notificaciones ni reintegros. Completá la prueba integral documentada antes de abrir ventas.");
        return ok ? 0 : 2;
    }
}
