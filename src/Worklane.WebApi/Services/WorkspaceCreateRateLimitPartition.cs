using Microsoft.IdentityModel.JsonWebTokens;

namespace Worklane.WebApi.Services;

/// <summary>
/// Partition key for <c>POST /api/workspaces</c>. The bucket is the JWT <c>sub</c>
/// claim only. It does not use <see cref="AttachmentRateLimitPartition"/>, which
/// appends a workspace id and would be <c>sub:unknown</c> on this route.
/// Forwarded headers are not read.
/// </summary>
internal static class WorkspaceCreateRateLimitPartition
{
    public static string Key(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return string.IsNullOrEmpty(sub) ? "anonymous" : sub;
    }
}
