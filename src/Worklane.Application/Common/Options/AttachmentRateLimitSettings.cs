namespace Worklane.Application.Common.Options;

/// <summary>
/// Fixed window for the "attachments" rate-limit policy. Comment creation uses its own
/// policy. The partition key is the caller's subject plus the task's workspace.
/// </summary>
public class AttachmentRateLimitSettings
{
    public const string SectionName = "RateLimit:Attachments";

    public int PermitLimit { get; set; } = 30;

    public int WindowSeconds { get; set; } = 60;
}
