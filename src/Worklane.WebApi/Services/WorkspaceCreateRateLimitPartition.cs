using Microsoft.IdentityModel.JsonWebTokens;

namespace Worklane.WebApi.Services;

/// <summary>
/// Partition key for <c>POST /api/workspaces</c>. The bucket is the access-token
/// subject. The client address is not part of the key: forwarded headers still
/// resolve the peer for the auth policy, and this key does not read
/// <c>X-Forwarded-For</c> or <c>Connection.RemoteIpAddress</c>.
/// </summary>
internal static class WorkspaceCreateRateLimitPartition
{
    public static string Key(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (Guid.TryParse(sub, out var userId))
            return userId.ToString("D");

        return "anonymous";
    }
}
