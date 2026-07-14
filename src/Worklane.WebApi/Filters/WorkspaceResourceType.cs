namespace Worklane.WebApi.Filters;

/// <summary>How RequireWorkspaceRoleAttribute should resolve the WorkspaceId from the route.</summary>
public enum WorkspaceResourceType
{
    /// <summary>The route parameter directly is the WorkspaceId (e.g. /api/workspaces/{id}/...).</summary>
    Workspace,

    /// <summary>The route parameter is a ProjectId; its WorkspaceId is looked up (e.g. /api/projects/{id}).</summary>
    Project
}
