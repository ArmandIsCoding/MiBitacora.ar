using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class AccountRecoveryService(UserManager<ApplicationUser> users, TransactionalMail mail,
    IDbContextFactory<ApplicationDbContext> factory, IOptions<SubscriptionOptions> settings, TimeProvider clock)
{
    public bool IsReady => mail.IsReady;
    public const string GenericMessage = "Si existe una cuenta con ese correo, recibirás un mensaje con los pasos para continuar. Revisá también el correo no deseado.";
    public async Task RequestAsync(string email, bool resetPassword, CancellationToken ct = default)
    {
        email = email.Trim();
        if (email.Length > 254 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            throw new JournalInputException("Ingresá un correo electrónico válido.");
        if (!IsReady) throw new JournalInputException("El correo está temporalmente fuera de servicio. Podés escribirnos a " + BusinessIdentity.SupportEmail + ".");
        var user = await users.FindByEmailAsync(email);
        if (user is null || (!resetPassword && user.EmailConfirmed)) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        if (await db.TransactionalEmails.CountAsync(row => row.Recipient == user.Email && row.Key.StartsWith("account-") && row.CreatedUtc > cutoff, ct) >= 4) return;
        var raw = resetPassword ? await users.GeneratePasswordResetTokenAsync(user) : await users.GenerateEmailConfirmationTokenAsync(user);
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
        var path = resetPassword ? "/Account/ResetPassword" : "/Account/ConfirmEmail";
        var link = settings.Value.PublicBaseUrl.TrimEnd('/') + path + "?email=" + Uri.EscapeDataString(user.Email!) + "&code=" + token;
        var title = resetPassword ? "Recuperá el acceso a Mi Bitácora" : "Confirmá tu correo en Mi Bitácora";
        var action = resetPassword ? "elegir una nueva contraseña" : "confirmar tu correo";
        await mail.QueueAsync("account-" + Guid.NewGuid().ToString("N"), user.Email!, title,
            $"Hola.\n\nUsá este enlace para {action}:\n{link}\n\nEl enlace vence en 2 horas. Si no lo pediste, podés ignorarlo. No compartas este mensaje ni tu contraseña.\n\nMi Bitácora · {BusinessIdentity.Name}\n{BusinessIdentity.SupportEmail}", ct);
    }
    public async Task<bool> ConfirmAsync(string email, string code)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null) return false;
        try { return (await users.ConfirmEmailAsync(user, Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)))).Succeeded; }
        catch (FormatException) { return false; }
    }
    public async Task<IdentityResult> ResetAsync(string email, string code, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null) return IdentityResult.Failed(new IdentityError { Description = "El enlace no es válido o venció. Pedí uno nuevo." });
        try { return await users.ResetPasswordAsync(user, Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)), password); }
        catch (FormatException) { return IdentityResult.Failed(new IdentityError { Description = "El enlace no es válido o venció. Pedí uno nuevo." }); }
    }
}
