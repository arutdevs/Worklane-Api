using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

/// <summary>
/// One member added, role change, or member removal inside a workspace. Card history stays on
/// <see cref="TaskActivity"/>; that row requires a task, so membership events are recorded here.
/// </summary>
public class WorkspaceMembershipEvent
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid TargetUserId { get; set; }
    public WorkspaceMembershipAction Action { get; set; }

    /// <summary>Null when <see cref="Action"/> is <see cref="WorkspaceMembershipAction.MemberAdded"/>.</summary>
    public WorkspaceRole? OldRole { get; set; }

    /// <summary>Null when <see cref="Action"/> is <see cref="WorkspaceMembershipAction.MemberRemoved"/>.</summary>
    public WorkspaceRole? NewRole { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
}
