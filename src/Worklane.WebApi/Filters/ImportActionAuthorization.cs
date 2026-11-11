using Worklane.Domain.Enums;

namespace Worklane.WebApi.Filters;

/// <summary>
/// One gate per import action. A later WL-ROLES change edits the minimum on that attribute.
/// </summary>
public sealed class AuthorizeImportUploadAttribute : RequireWorkspaceRoleAttribute
{
    // TODO(WL-ROLES): upload minimum is Member.
    public AuthorizeImportUploadAttribute()
        : base(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")
    {
    }
}

public sealed class AuthorizeImportCommitAttribute : RequireWorkspaceRoleAttribute
{
    // TODO(WL-ROLES): commit minimum is Member.
    public AuthorizeImportCommitAttribute()
        : base(WorkspaceRole.Member, WorkspaceResourceType.Project, "projectId")
    {
    }
}

public sealed class AuthorizeImportUndoAttribute : RequireWorkspaceRoleAttribute
{
    // TODO(WL-ROLES): undo minimum is Admin.
    public AuthorizeImportUndoAttribute()
        : base(WorkspaceRole.Admin, WorkspaceResourceType.Project, "projectId")
    {
    }
}

public sealed class AuthorizeImportExportAttribute : RequireWorkspaceRoleAttribute
{
    // TODO(WL-ROLES): export minimum is Viewer.
    public AuthorizeImportExportAttribute()
        : base(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")
    {
    }
}
