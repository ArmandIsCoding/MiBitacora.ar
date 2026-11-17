using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class CustomerRequestService(IDbContextFactory<ApplicationDbContext> factory, UserManager<ApplicationUser> users,
    TransactionalMail mail, IOptions<SubscriptionOptions> settings, TimeProvider clock, ILogger<CustomerRequestService>? logger = null)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public async Task<string> CreateAsync(string email, string kind, string message, string? authenticatedUserId,
        CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant(); message = (message ?? "").Trim();
        if (email.Length > 254 || !new EmailAddressAttribute().IsValid(email) || kind is not ("cancel" or "withdraw" or "support") || message.Length > 2000)
            throw new JournalInputException("Revisá el correo y el texto de la solicitud (hasta 2000 caracteres).");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.CustomerRequests.CountAsync(row => row.Email == email && row.CreatedUtc > Now.AddHours(-1), ct) >= 4)
            throw new JournalInputException("Ya recibimos varias solicitudes con ese correo. Podés usar el código que te mostramos o escribirnos directamente.");
        var user = await users.FindByEmailAsync(email);
        var verified = user is not null && user.Id == authenticatedUserId;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var request = new CustomerRequestRow { Id = Guid.NewGuid(), Code = "MB-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
            Email = email, UserId = user?.Id, Kind = kind, Message = message, CreatedUtc = Now,
            VerifiedUtc = verified ? Now : null, VerificationHash = verified ? null : TokenHash(token),
            VerificationExpiresUtc = verified ? null : Now.AddDays(3), Status = verified && kind != "support" ? "processing" : "received" };
        db.CustomerRequests.Add(request);
        await db.SaveChangesAsync(ct); // Receiving a request never depends on SMTP availability or registration.
        try
        {
          if (mail.IsReady) {
            var link = settings.Value.PublicBaseUrl.TrimEnd('/') + "/solicitud/" + request.Code;
            var confirmation = verified ? "Tu identidad quedó verificada con tu sesión." :
                "Para verificar tu identidad y permitirnos gestionar los cobros de forma segura, abrí este enlace y confirmá la solicitud:\n" + link + "?token=" + token;
            await mail.QueueAsync("request-received-" + request.Id, email, "Recibimos tu solicitud " + request.Code,
                $"Recibimos tu solicitud de {KindLabel(kind)}.\nCódigo: {request.Code}\nFecha: {Now:yyyy-MM-dd HH:mm} UTC\n\n{confirmation}\n\nPodés seguir el estado en {link}. No necesitás registrarte ni ingresar. Si el enlace venciera, escribinos citando este código; la fecha original de tu solicitud se conserva.\n\n{BusinessIdentity.Name}\n{BusinessIdentity.SupportEmail}", ct);
            await mail.QueueAsync("request-alert-" + request.Id, BusinessIdentity.SupportEmail, "Mi Bitácora · solicitud " + request.Code,
                $"Se recibió una solicitud de {KindLabel(kind)}. Código {request.Code}. Revisala en /admin/suscripciones. No se adjunta contenido del cuaderno.", ct);
          }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger?.LogWarning("La solicitud se recibió; sus avisos de correo requieren revisión en administración."); }
        return request.Code;
    }

    public async Task<CustomerRequestRow?> GetReceiptAsync(string code, CancellationToken ct = default)
    {
        if (code.Length > 40) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.CustomerRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Code == code, ct);
    }
    public static string KindLabel(string kind) => kind switch { "cancel" => "baja del servicio", "withdraw" => "arrepentimiento y reintegro", _ => "consulta o reclamo" };
    public static string StatusLabel(string status) => status switch
    { "completed" => "Resuelta", "processing" => "En gestión", "needs_review" => "En revisión por atención al cliente", _ => "Recibida" };
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public bool CanVerify(CustomerRequestRow row, string? token) => token is { Length: 64 } && row.VerificationHash is { } expected &&
        row.VerificationExpiresUtc >= Now && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(TokenHash(token)));
    public async Task<bool> ConfirmAsync(string code, string token, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.CustomerRequests.SingleOrDefaultAsync(row => row.Code == code, ct);
        if (row is null || !CanVerify(row, token)) return false;
        row.VerifiedUtc = Now; row.VerificationHash = null; row.VerificationExpiresUtc = null;
        row.Status = row.Kind == "support" ? "needs_review" : "processing";
        await db.SaveChangesAsync(ct);
        return true;
    }
}
