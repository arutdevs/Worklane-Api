using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Worklane.Infrastructure.Persistence;

namespace Worklane.WebApi.Services;

/// <summary>
/// Limiter key for import and export: JWT subject plus the workspace that owns the route
/// project. The limiter runs before a tenant is pinned, so the lookup ignores filters and
/// keeps the project id as the predicate. Same shape as <see cref="AttachmentRateLimitPartition"/>.
/// </summary>
internal static class ImportRateLimitPartition
{
    public static string Key(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(sub))
            sub = "anonymous";

        var workspace = "unknown";
        if (TryGetProjectId(httpContext, out var projectId))
            workspace = WorkspaceForProject(httpContext, projectId);
        return sub + ":" + workspace;
    }

    private static string WorkspaceForProject(HttpContext httpContext, Guid projectId)
    {
        var cache = httpContext.RequestServices.GetService<IMemoryCache>();
        var cacheKey = "project-workspace:" + projectId.ToString("N");
        if (cache is not null && cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        var db = httpContext.RequestServices.GetRequiredService<WorklaneDbContext>();
        var id = db.Projects
            .IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.WorkspaceId)
            .FirstOrDefault();
        var workspace = id is Guid workspaceId ? workspaceId.ToString("N") : "unknown";
        if (cache is not null && workspace != "unknown")
        {
            cache.Set(cacheKey, workspace, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            });
        }

        return workspace;
    }

    private static bool TryGetProjectId(HttpContext httpContext, out Guid projectId)
    {
        if (httpContext.Request.RouteValues.TryGetValue("projectId", out var raw) &&
            Guid.TryParse(raw?.ToString(), out projectId))
            return true;

        projectId = Guid.Empty;
        return false;
    }
}
