using Microsoft.AspNetCore.Identity;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class AccountDeletionService(UserManager<ApplicationUser> users, UploadFileStore uploads, SubscriptionService? subscriptions = null)
{
    public async Task<IdentityResult> DeleteAsync(ApplicationUser user)
    {
        var accountGate = subscriptions?.AccountGate(user.Id);
        if (accountGate is not null) await accountGate.WaitAsync();
        try
        {
            if (subscriptions is not null)
            {
                try { await subscriptions.CancelForDeletionAsync(user.Id); }
                catch (Exception ex) when (ex is SubscriptionException or HttpRequestException or TaskCanceledException)
                {
                    return IdentityResult.Failed(new IdentityError { Code = "SubscriptionCancellationPending",
                        Description = "No pudimos confirmar la cancelación de los cobros. Reintentá desde Mi plan antes de borrar la cuenta." });
                }
            }
            using var uploadLock = await uploads.LockAsync();
            var result = await users.DeleteAsync(user);
            if (result.Succeeded) uploads.DeleteAccount(user.Id);
            return result;
        }
        finally { accountGate?.Release(); }
    }
}
