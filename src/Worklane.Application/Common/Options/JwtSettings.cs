namespace Worklane.Application.Common.Options;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Older keys still accepted until their access tokens expire. New tokens are signed
    /// with <see cref="Secret"/> only. Each value is base64, same as <see cref="Secret"/>.
    /// </summary>
    public string[] PreviousSecrets { get; set; } = [];

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int AccessTokenExpiryMinutes { get; set; } = 15;
    public int RefreshTokenExpiryDays { get; set; } = 7;
}
