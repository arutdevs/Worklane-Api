namespace Worklane.Application.Common.Options;

/// <summary>
/// Fixed window for the "workspace-create" rate-limit policy. Only
/// <c>POST /api/workspaces</c> uses it. The partition key is the authenticated
/// user id, not the client IP.
/// </summary>
public class WorkspaceCreateRateLimitSettings
{
    public const string SectionName = "RateLimit:WorkspaceCreate";

    public const string PolicyName = "workspace-create";

    public int PermitLimit { get; set; } = 5;

    public int WindowSeconds { get; set; } = 3600;
}
