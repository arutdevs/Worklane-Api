using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;

namespace Worklane.Application.Workspaces.Interfaces;

public interface IWorkspaceService
{
    Task<List<WorkspaceDto>> GetMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken);
    Task<WorkspaceDto> CreateWorkspaceAsync(Guid ownerUserId, CreateWorkspaceRequest request, CancellationToken cancellationToken);
    Task<List<WorkspaceMemberDto>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<InvitationDto> InviteMemberAsync(Guid workspaceId, Guid invitedByUserId, InviteMemberRequest request, CancellationToken cancellationToken);
    Task AcceptInvitationAsync(Guid acceptingUserId, string token, CancellationToken cancellationToken);
    /// <summary>
    /// <paramref name="actorUserId"/> identifies the caller. Their role is loaded from
    /// <c>WorkspaceMembers</c> for <paramref name="workspaceId"/>, not from the token or body.
    /// </summary>
    Task UpdateMemberRoleAsync(Guid workspaceId, Guid actorUserId, Guid targetUserId, WorkspaceRole newRole, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="actorUserId"/> identifies the caller. Their role is loaded from
    /// <c>WorkspaceMembers</c> for <paramref name="workspaceId"/>, not from the token or body.
    /// </summary>
    Task RemoveMemberAsync(Guid workspaceId, Guid actorUserId, Guid targetUserId, CancellationToken cancellationToken);

    /// <summary>Used by RequireWorkspaceRoleAttribute to authorize a request. Returns null if not a member.</summary>
    Task<WorkspaceRole?> GetUserRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken);
}
