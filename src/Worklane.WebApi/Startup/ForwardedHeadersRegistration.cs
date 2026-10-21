using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Worklane.WebApi.Middleware;

namespace Worklane.WebApi.Startup;

public static class ForwardedHeadersRegistration
{
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    public static IServiceCollection AddWorklaneForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // One hop, from the right. "spoof, client" from a trusted proxy is the client.
            // A wider limit would walk a trusted network into the caller-supplied address.
            options.ForwardLimit = 1;

            var proxies = ReadList(configuration, KnownProxiesKey);
            var networks = ReadList(configuration, KnownNetworksKey);
            if (proxies.Count == 0 && networks.Count == 0)
                return;

            var parsedProxies = new List<IPAddress>(proxies.Count);
            foreach (var value in proxies)
            {
                if (!IPAddress.TryParse(value, out var ip))
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownProxies value '{value}' is not an IP address.");
                }

                parsedProxies.Add(ip);
            }

            var parsedNetworks = new List<System.Net.IPNetwork>(networks.Count);
            foreach (var value in networks)
            {
                if (!System.Net.IPNetwork.TryParse(value, out var network))
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownNetworks value '{value}' is not a CIDR prefix.");
                }

                var hostPrefix = network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                if (network.PrefixLength != hostPrefix)
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownNetworks value '{value}' trusts more than one address. Put that proxy in {KnownProxiesKey}.");
                }

                parsedNetworks.Add(network);
            }

            // The framework default is loopback only. Docker's nginx address is not
            // loopback, and an empty list would trust every peer. A configured list
            // replaces the default.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var ip in parsedProxies)
                options.KnownProxies.Add(ip);
            foreach (var network in parsedNetworks)
                options.KnownIPNetworks.Add(network);
        });

        return services;
    }

    public static IApplicationBuilder UseWorklaneForwardedHeaders(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            ForwardedClientIp.Capture(context, options);
            await next(context);
        });
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            ForwardedClientIp.Apply(context);
            await next(context);
        });
        return app;
    }

    private static List<string> ReadList(IConfiguration configuration, string key)
    {
        var values = configuration.GetSection(key).Get<string[]>() ?? [];
        var list = new List<string>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"{key} contains an empty entry.");

            list.Add(value.Trim());
        }

        return list;
    }
}
