using Microsoft.AspNetCore.Identity;

namespace MiBitacora.Data;

public class ApplicationUser : IdentityUser
{
    public DateTime? RegisteredUtc { get; set; }
}
