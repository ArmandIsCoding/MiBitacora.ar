using Microsoft.AspNetCore.Identity;

namespace MiBitacora.Data;

public class ApplicationUser : IdentityUser
{
    public DateTime? RegisteredUtc { get; set; }
    public string? AcceptedTermsVersion { get; set; }
    public DateTime? AcceptedTermsUtc { get; set; }
}
