namespace MiBitacora.Services;

public static class BillingRegistration
{
    public static void AddBilling(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<SubscriptionOptions>().Bind(builder.Configuration.GetSection("Subscriptions"))
            .Validate(o => o.IsValid(builder.Environment.IsDevelopment()), "La configuración de suscripciones no es válida para este entorno.")
            .ValidateOnStart();
        builder.Services.AddSingleton<BillingGate>();
        builder.Services.AddScoped<SubscriptionService>();
        builder.Services.AddScoped<BillingWebhookInbox>();
        builder.Services.AddHttpClient<ISubscriptionGateway, MercadoPagoGateway>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        builder.Services.AddHostedService<BillingWorker>();
    }
}
