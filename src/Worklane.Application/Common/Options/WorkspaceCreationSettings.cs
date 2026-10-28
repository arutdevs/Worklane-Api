namespace Worklane.Application.Common.Options;

/// <summary>
/// Limits for <c>POST /api/workspaces</c>. The rate-limit policy is a fixed window
/// partitioned by the access-token subject. <see cref="MaxOwnedWorkspaces"/> is the
/// ownership cap enforced when creating a workspace, not during registration.
/// </summary>
public class WorkspaceCreationSettings
{
    public const string SectionName = "WorkspaceCreation";

    public const string PolicyName = "workspace-create";

    public int PermitLimit { get; set; } = 10;

    public int WindowSeconds { get; set; } = 3600;

    public int MaxOwnedWorkspaces { get; set; } = 20;
}
