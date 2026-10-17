using Worklane.Application.Common.Exceptions;
using Worklane.Domain.Enums;

namespace Worklane.Application.Workspaces;

/// <summary>
/// Who may change or remove a workspace member. Callers pass the actor role loaded from
/// <c>WorkspaceMembers</c> for the workspace in the route. This type does not read the
/// request body or the caller's membership in any other workspace.
/// </summary>
public static class WorkspaceMembershipPolicy
{
    /// <summary>
    /// Single switch for the undecided rule: may an Admin change or remove a <em>different</em> Admin?
    /// <para><c>false</c> (current): those calls are forbidden. Only an Owner can.</para>
    /// <para><c>true</c>: an Admin may demote or remove a peer Admin. Granting Owner stays forbidden,
    /// and <see cref="AdminMayGrantAdmin"/> is a separate decision.</para>
    /// <see cref="EnsureCanManage"/> is the only reader. Tests branch on this value.
    /// </summary>
    public const bool AdminMayManagePeerAdmins = false;

    /// <summary>
    /// Whether an Admin may set a Viewer or Member to <see cref="WorkspaceRole.Admin"/>.
    /// Kept <c>false</c>: an Admin manages only Viewer and Member roles, so creating another
    /// Admin is forbidden. WL-62 acceptance criteria #2 and #12 ask for 204 on Member → Admin;
    /// that promotion stays off until this constant is set to <c>true</c>. Granting Owner is
    /// never allowed for an Admin, whatever this value is.
    /// </summary>
    public const bool AdminMayGrantAdmin = false;

    /// <summary>
    /// Authorize a role change, or a removal when <paramref name="newRole"/> is null.
    /// Owners pass. The last-Owner guard is not applied here.
    /// </summary>
    public static void EnsureCanManage(WorkspaceRole actorRole, WorkspaceRole targetRole, WorkspaceRole? newRole, bool isSelf)
    {
        if (actorRole == WorkspaceRole.Owner)
            return;

        if (actorRole != WorkspaceRole.Admin)
            Deny("You do not have permission to manage workspace members.");

        if (targetRole == WorkspaceRole.Owner || newRole == WorkspaceRole.Owner)
            Deny("Only an Owner can grant, change, or remove the Owner role.");

        if (isSelf)
        {
            if (newRole is null)
                Deny("Only an Owner can remove an Admin.");

            // Same role is a no-op. A higher role would be a self-promotion.
            if (newRole > targetRole)
                Deny("You cannot raise your own role.");

            return;
        }

        if (targetRole == WorkspaceRole.Admin)
        {
            if (!AdminMayManagePeerAdmins)
                Deny("Only an Owner can change or remove another Admin.");

            return;
        }

        if (targetRole is not (WorkspaceRole.Viewer or WorkspaceRole.Member))
            Deny("An Admin can only manage Viewers and Members.");

        if (newRole == WorkspaceRole.Admin && !AdminMayGrantAdmin)
            Deny("Only an Owner can grant the Admin role.");
    }

    private static void Deny(string message) => throw new ForbiddenAccessException(message);
}
