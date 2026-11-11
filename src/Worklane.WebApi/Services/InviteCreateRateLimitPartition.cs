using Microsoft.IdentityModel.JsonWebTokens;

namespace Worklane.WebApi.Services;

/// <summary>
/// Partition key for <c>POST /api/workspaces/{id}/invitations</c>. The bucket is the
/// JWT <c>sub</c> claim (the authenticated user id). Forwarded headers and the client
/// address are not read. The workspace id is not part of the key: one caller shares
/// one window across workspaces, and another caller does not.
/// </summary>
internal static class InviteCreateRateLimitPartition
{
    public static string Key(HttpContext httpContext)
    {
        var sub = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return string.IsNullOrEmpty(sub) ? "anonymous" : sub;
    }
}
