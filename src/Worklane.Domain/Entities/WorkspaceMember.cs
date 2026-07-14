using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

public class WorkspaceMember
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public WorkspaceRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
}
