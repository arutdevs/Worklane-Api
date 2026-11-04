using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

public class Invitation
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Email { get; set; } = string.Empty;
    public WorkspaceRole Role { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public Guid InvitedById { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
}
