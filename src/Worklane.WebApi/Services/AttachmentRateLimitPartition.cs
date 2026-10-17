using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Worklane.Infrastructure.Persistence;

namespace Worklane.WebApi.Services;

/// <summary>
/// Builds the "attachments" limiter key from the JWT subject and the workspace that owns
/// the task in the route. A missing workspace still gets a key, so the request is limited.
/// </summary>
internal static class AttachmentRateLimitPartition
{
    public static string Key(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(sub))
            sub = "anonymous";

        var workspace = "unknown";
        if (TryGetTaskId(httpContext, out var taskId))
        {
            var db = httpContext.RequestServices.GetRequiredService<WorklaneDbContext>();
            var id = db.TaskItems
                .IgnoreQueryFilters()
                .Where(t => t.Id == taskId)
                .Select(t => (Guid?)t.Project.WorkspaceId)
                .FirstOrDefault();
            if (id is Guid workspaceId)
                workspace = workspaceId.ToString("N");
        }

        return sub + ":" + workspace;
    }

    private static bool TryGetTaskId(HttpContext httpContext, out Guid taskId)
    {
        if (httpContext.Request.RouteValues.TryGetValue("taskId", out var raw) &&
            Guid.TryParse(raw?.ToString(), out taskId))
            return true;

        var path = httpContext.Request.Path.Value;
        const string marker = "/api/tasks/";
        if (path is not null)
        {
            var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var rest = path[(index + marker.Length)..];
                var slash = rest.IndexOf('/');
                var token = slash >= 0 ? rest[..slash] : rest;
                if (Guid.TryParse(token, out taskId))
                    return true;
            }
        }

        taskId = Guid.Empty;
        return false;
    }
}
