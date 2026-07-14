using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Projects.Interfaces;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Enums;

namespace Worklane.WebApi.Filters;

/// <summary>
/// Verifies the current user is a member of the target workspace with at least MinimumRole,
/// reading the WorkspaceId (or ProjectId, resolved to its WorkspaceId) from the route — never
/// from the request body. On success, pins ICurrentTenantService to that workspace so every
/// EF Core query for the rest of the request is guarded by the tenant query filter too.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequireWorkspaceRoleAttribute : Attribute, IAsyncActionFilter
{
    private readonly WorkspaceRole _minimumRole;
    private readonly WorkspaceResourceType _resourceType;
    private readonly string _routeParam;

    public RequireWorkspaceRoleAttribute(
        WorkspaceRole minimumRole,
        WorkspaceResourceType resourceType = WorkspaceResourceType.Workspace,
        string routeParam = "id")
    {
        _minimumRole = minimumRole;
        _resourceType = resourceType;
        _routeParam = routeParam;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var currentUserService = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserService>();
        var workspaceService = context.HttpContext.RequestServices.GetRequiredService<IWorkspaceService>();
        var tenantService = context.HttpContext.RequestServices.GetRequiredService<ICurrentTenantService>();

        var userId = currentUserService.UserId;
        if (userId == Guid.Empty)
        {
            context.Result = Problem(context, StatusCodes.Status401Unauthorized, "Unauthorized", "A valid access token is required.");
            return;
        }

        if (!context.RouteData.Values.TryGetValue(_routeParam, out var routeValue) ||
            !Guid.TryParse(routeValue?.ToString(), out var routeId))
        {
            context.Result = Problem(context, StatusCodes.Status400BadRequest, "Bad Request", $"Route parameter \"{_routeParam}\" is missing or not a valid id.");
            return;
        }

        Guid workspaceId;
        if (_resourceType == WorkspaceResourceType.Workspace)
        {
            workspaceId = routeId;
        }
        else
        {
            var projectService = context.HttpContext.RequestServices.GetRequiredService<IProjectService>();
            var resolved = await projectService.GetWorkspaceIdForProjectAsync(routeId, context.HttpContext.RequestAborted);
            if (resolved is null)
            {
                context.Result = Problem(context, StatusCodes.Status404NotFound, "Not Found", "Project was not found.");
                return;
            }
            workspaceId = resolved.Value;
        }

        var role = await workspaceService.GetUserRoleAsync(workspaceId, userId, context.HttpContext.RequestAborted);
        if (role is null || role < _minimumRole)
        {
            context.Result = Problem(context, StatusCodes.Status403Forbidden, "Forbidden", "You do not have sufficient permissions in this workspace.");
            return;
        }

        tenantService.SetWorkspace(workspaceId);

        await next();
    }

    private static ObjectResult Problem(ActionExecutingContext context, int statusCode, string title, string detail)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.HttpContext.Request.Path
        };
        return new ObjectResult(problemDetails) { StatusCode = statusCode };
    }
}
