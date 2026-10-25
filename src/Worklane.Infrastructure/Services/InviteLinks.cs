namespace Worklane.Infrastructure.Services;

internal static class InviteLinks
{
    public static string ForToken(string? baseUrl, string token)
    {
        var path = "/invitations/accept?token=" + Uri.EscapeDataString(token);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return path;

        return baseUrl.Trim().TrimEnd('/') + path;
    }
}
