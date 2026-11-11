namespace Worklane.Application.Common.Options;

/// <summary>
/// Account lockout applied by Identity on failed logins.
/// Bound from the <c>Lockout</c> configuration section.
/// </summary>
public class LockoutSettings
{
    public const string SectionName = "Lockout";

    public int MaxFailedAccessAttempts { get; set; } = 5;

    public TimeSpan DefaultLockoutTimeSpan { get; set; } = TimeSpan.FromMinutes(15);

    public bool AllowedForNewUsers { get; set; } = true;
}
