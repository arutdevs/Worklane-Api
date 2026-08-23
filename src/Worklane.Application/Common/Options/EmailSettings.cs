namespace Worklane.Application.Common.Options;

public class EmailSettings
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;

    /// <summary>Also used as the From address — Gmail (and most SMTP relays) require the
    /// authenticated account and the From address to match.</summary>
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromName { get; set; } = "Worklane";
}
