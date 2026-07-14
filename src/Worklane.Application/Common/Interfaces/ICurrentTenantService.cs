namespace Worklane.Application.Common.Interfaces;

/// <summary>
/// Scoped per-request tenant context. Set by RequireWorkspaceRoleFilter once membership
/// is verified, then read by the EF Core global query filters so any query that forgets
/// an explicit WorkspaceId predicate still cannot leak cross-tenant rows.
/// </summary>
public interface ICurrentTenantService
{
    Guid? WorkspaceId { get; }
    void SetWorkspace(Guid workspaceId);
}
