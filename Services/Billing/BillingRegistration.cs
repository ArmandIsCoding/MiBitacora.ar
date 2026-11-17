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
        builder.Services.AddScoped<AiUsageService>();
        builder.Services.AddOptions<MailOptions>().Bind(builder.Configuration.GetSection("Mail"))
            .Validate(options => options.IsValid(builder.Environment.IsDevelopment()), "La configuración de correo no es válida.").ValidateOnStart();
        builder.Services.AddScoped<TransactionalMail>();
        builder.Services.AddScoped<AccountRecoveryService>();
        builder.Services.AddScoped<CustomerRequestService>();
        builder.Services.AddHostedService<TransactionalMailWorker>();
        builder.Services.AddScoped<BillingWebhookInbox>();
        builder.Services.AddHttpClient<ISubscriptionGateway, MercadoPagoGateway>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        builder.Services.AddHostedService<BillingWorker>();
    }
}
