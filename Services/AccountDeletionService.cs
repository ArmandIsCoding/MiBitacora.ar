using Microsoft.AspNetCore.Identity;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed class AccountDeletionService(UserManager<ApplicationUser> users, UploadFileStore uploads)
{
    public async Task<IdentityResult> DeleteAsync(ApplicationUser user)
    {
        using var uploadLock = await uploads.LockAsync();
        var result = await users.DeleteAsync(user);
        if (result.Succeeded) uploads.DeleteAccount(user.Id);
        return result;
    }
}
