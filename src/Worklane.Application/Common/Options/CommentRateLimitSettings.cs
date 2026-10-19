namespace Worklane.Application.Common.Options;

/// <summary>
/// Fixed window for the "comments" rate-limit policy. Separate from attachment uploads
/// so a busy comment thread does not consume the upload budget.
/// </summary>
public class CommentRateLimitSettings
{
    public const string SectionName = "RateLimit:Comments";

    public int PermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;
}
