using Microsoft.AspNetCore.Identity;

namespace Worklane.Infrastructure.Identity;

public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string PreferredLanguage { get; set; } = "en";
}
