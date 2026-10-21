namespace Worklane.Application.Common.Options;

/// <summary>
/// Fixed window for the "auth" rate-limit policy. Login, register, and refresh share
/// one bucket per client IP. Logout is not limited.
/// </summary>
public class AuthRateLimitSettings
{
    public const string SectionName = "RateLimit:Auth";

    public const string PolicyName = "auth";

    public int PermitLimit { get; set; } = 10;

    public int WindowSeconds { get; set; } = 60;
}
