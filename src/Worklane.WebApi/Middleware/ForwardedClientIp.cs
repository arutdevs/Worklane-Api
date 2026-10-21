using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;

namespace Worklane.WebApi.Middleware;

/// <summary>
/// Resolves the address the auth limiter partitions on. <see cref="Capture"/> runs before
/// <c>UseForwardedHeaders</c> and remembers <c>CF-Connecting-IP</c> only when the TCP peer
/// is a configured proxy. <see cref="Apply"/> runs after forwarded headers, so
/// <c>X-Forwarded-Proto</c> still updates the scheme, and overwrites the client address
/// with that Cloudflare header. A peer that is not in <c>KnownProxies</c> or
/// <c>KnownNetworks</c> keeps its socket address; spoofed headers are ignored.
/// </summary>
internal static class ForwardedClientIp
{
    public const string CloudflareConnectingIp = "CF-Connecting-IP";

    private const string ItemKey = "Worklane.TrustedCfConnectingIp";

    public static void Capture(HttpContext context, ForwardedHeadersOptions options)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !IsTrustedProxy(peer, options))
            return;

        if (!TryParseCloudflare(context.Request.Headers[CloudflareConnectingIp], out var client))
            return;

        context.Items[ItemKey] = client;
    }

    public static void Apply(HttpContext context)
    {
        if (context.Items[ItemKey] is IPAddress client)
            context.Connection.RemoteIpAddress = client;
    }

    public static string PartitionKey(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip is null)
            return "unknown";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return ip.ToString();
    }

    private static bool TryParseCloudflare(StringValues raw, out IPAddress client)
    {
        client = IPAddress.None;
        if (raw.Count == 0)
            return false;

        var text = raw.ToString().Trim();
        if (text.Length == 0 || text.Contains(','))
            return false;

        if (!IPAddress.TryParse(text, out var parsed))
            return false;

        client = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    private static bool IsTrustedProxy(IPAddress address, ForwardedHeadersOptions options)
    {
        if (address.IsIPv4MappedToIPv6 && IsTrustedProxy(address.MapToIPv4(), options))
            return true;

        if (options.KnownProxies.Contains(address))
            return true;

        foreach (var network in options.KnownIPNetworks)
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }
}
