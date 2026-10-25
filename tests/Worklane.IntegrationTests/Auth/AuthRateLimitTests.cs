using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.IntegrationTests.Infrastructure;
using Worklane.WebApi.Startup;

namespace Worklane.IntegrationTests.Auth;

public class AuthRateLimitTests : IClassFixture<AuthRateLimitFactory>
{
    private readonly AuthRateLimitFactory _factory;

    public AuthRateLimitTests(AuthRateLimitFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LoginRegisterAndRefresh_AreLimitedPerIp()
    {
        var client = _factory.CreateClient();
        const string limited = "10.8.0.1";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, limited);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, limited);
        await AssertRateLimited(blocked);

        var otherIp = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, "10.8.0.2");
        otherIp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        const string shared = "10.8.0.3";
        var email = $"limited-{Guid.NewGuid():N}@example.com";
        (await Post(client, "/api/auth/register", new { email, password = "Password123!", displayName = "Ada" }, shared))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await Post(client, "/api/auth/login", new { email, password = "WrongPassword1" }, shared))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Post(client, "/api/auth/refresh", new { refreshToken = "not-a-real-token" }, shared))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var sharedBlocked = await Post(client, "/api/auth/login", new { email, password = "WrongPassword1" }, shared);
        sharedBlocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        const string logoutIp = "10.8.0.4";
        for (var i = 0; i < 5; i++)
        {
            var logout = await Post(client, "/api/auth/logout", new { refreshToken = "not-a-real-token" }, logoutIp);
            logout.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        var afterLogout = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, logoutIp);
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ClientsBehindTrustedProxy_WithDifferentForwardedFor_GetSeparateBuckets()
    {
        var client = _factory.CreateClient();
        const string first = "203.0.113.10";
        const string second = "203.0.113.11";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, AuthRateLimitFactory.TrustedProxy, first);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, AuthRateLimitFactory.TrustedProxy, first);
        await AssertRateLimited(blocked);

        var otherClient = await Post(client, "/api/auth/login", new { email = "nobody@example.com", password = "WrongPassword1" }, AuthRateLimitFactory.TrustedProxy, second);
        otherClient.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CfConnectingIp_IsHonouredOnlyFromTheTrustedProxy()
    {
        var client = _factory.CreateClient();
        const string sharedForwardedFor = "203.0.113.80";
        const string first = "203.0.113.81";
        const string second = "203.0.113.82";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                AuthRateLimitFactory.TrustedProxy,
                sharedForwardedFor,
                first);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedProxy,
            sharedForwardedFor,
            first);
        await AssertRateLimited(blocked);

        var otherClient = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedProxy,
            sharedForwardedFor,
            second);
        otherClient.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Same subnet as the proxy, not the proxy. A configured /24 would have trusted it.
        const string neighbor = "10.50.0.8";
        const string otherNeighbor = "10.50.0.9";
        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                neighbor,
                $"203.0.113.{100 + i}",
                $"198.51.100.{100 + i}");
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var neighborBlocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            neighbor,
            "203.0.113.199",
            "198.51.100.199");
        await AssertRateLimited(neighborBlocked);

        var otherNeighborResponse = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            otherNeighbor,
            "203.0.113.100",
            "198.51.100.100");
        otherNeighborResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SpoofedForwardedForOrCfConnectingIp_FromUntrustedPeer_IsIgnored()
    {
        var client = _factory.CreateClient();
        const string peer = "198.51.100.20";
        const string otherPeer = "198.51.100.21";
        const string spoofedForwardedFor = "203.0.113.50";
        const string spoofedCf = "203.0.113.51";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                peer,
                spoofedForwardedFor,
                spoofedCf);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            peer,
            "203.0.113.60",
            "203.0.113.61");
        await AssertRateLimited(blocked);

        var otherPeerResponse = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            otherPeer,
            spoofedForwardedFor,
            spoofedCf);
        otherPeerResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebNginxAddress_IsNotTrusted_AndSpoofedForwardedForIsIgnored()
    {
        var client = _factory.CreateClient();
        const string webNginx = "172.30.20.3";
        const string otherPeer = "172.30.20.4";
        const string spoofed = "203.0.113.77";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                webNginx,
                $"198.51.100.{i + 1}");
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            webNginx,
            spoofed);
        await AssertRateLimited(blocked);

        var otherPeerResponse = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            otherPeer,
            spoofed);
        otherPeerResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var throughTrustedProxy = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedProxy,
            spoofed);
        throughTrustedProxy.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SpoofedSingleValueForwardedFor_FromUntrustedPeer_StillHitsTheLimit()
    {
        var client = _factory.CreateClient();
        const string peer = "198.51.100.40";
        const string otherPeer = "198.51.100.41";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                peer,
                $"203.0.113.{i + 1}",
                $"198.51.100.{i + 70}");
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            peer,
            "203.0.113.250");
        await AssertRateLimited(blocked);

        var other = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            otherPeer,
            "203.0.113.1");
        other.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SpoofedMultiValueForwardedFor_FromUntrustedPeer_FallsBackToTheRemoteAddress()
    {
        var client = _factory.CreateClient();
        const string peer = "198.51.100.42";
        const string otherPeer = "198.51.100.43";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                peer,
                $"1.2.3.{i + 1}, 203.0.113.{i + 20}");
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            peer,
            "9.9.9.9, 8.8.8.8, 203.0.113.77");
        await AssertRateLimited(blocked);

        var other = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            otherPeer,
            "1.2.3.1, 203.0.113.20");
        other.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExtraHopsFromTrustedProxy_DoNotBypassTheClientBucket()
    {
        var client = _factory.CreateClient();
        const string clientA = "203.0.113.30";
        const string clientB = "203.0.113.31";

        for (var i = 0; i < 3; i++)
        {
            var forwardedFor = i % 2 == 0
                ? $"1.2.3.{i + 1}, {clientA}"
                : $"1.2.3.{i + 1}, 9.9.9.{i + 1}, {clientA}";
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                AuthRateLimitFactory.TrustedProxy,
                forwardedFor,
                $"8.8.8.{i + 1}, 7.7.7.{i + 1}");
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedProxy,
            $"4.4.4.4, 5.5.5.5, {clientA}",
            "6.6.6.6, 203.0.113.99");
        await AssertRateLimited(blocked);

        var otherClient = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedProxy,
            $"4.4.4.4, 5.5.5.5, {clientB}");
        otherClient.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void ForwardLimit_IsOne_AndTheProxyNetworkIsNotTrusted()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        options.ForwardLimit.Should().Be(1);
        options.KnownProxies.Should().ContainSingle()
            .Which.Should().Be(IPAddress.Parse(AuthRateLimitFactory.TrustedProxy));
        options.KnownIPNetworks.Should().BeEmpty();
    }

    [Fact]
    public async Task IPv4MappedTrustedProxy_HonoursOneHop_AndDoesNotShareTheProxyBucket()
    {
        var client = _factory.CreateClient();
        var mappedProxy = "::ffff:" + AuthRateLimitFactory.TrustedProxy;
        const string first = "203.0.113.120";
        const string second = "203.0.113.121";

        for (var i = 0; i < 3; i++)
        {
            var attempt = await Post(
                client,
                "/api/auth/login",
                new { email = "nobody@example.com", password = "WrongPassword1" },
                mappedProxy,
                first);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            mappedProxy,
            first);
        await AssertRateLimited(blocked);

        var otherClient = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            mappedProxy,
            second);
        otherClient.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task AssertRateLimited(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.RetryAfter!.Delta.Should().NotBeNull();
        response.Headers.RetryAfter.Delta!.Value.TotalSeconds.Should().BeGreaterThan(0);
        body.Should().NotContain("retryAfterSeconds");
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("rate_limited");
        problem.GetProperty("scope").GetString().Should().Be("ip");
        problem.GetProperty("detail").GetString().Should().Be("Too many requests.");
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        string path,
        object body,
        string ip,
        string? forwardedFor = null,
        string? cfConnectingIp = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation(ClientIpFromHeaderStartupFilter.HeaderName, ip);
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        if (cfConnectingIp is not null)
            request.Headers.TryAddWithoutValidation("CF-Connecting-IP", cfConnectingIp);
        return await client.SendAsync(request);
    }
}

public class AuthRateLimitFactory : WorklaneWebApplicationFactory
{
    public const string TrustedProxy = "10.50.0.1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Auth:PermitLimit"] = "3",
                ["RateLimit:Auth:WindowSeconds"] = "60",
                ["ForwardedHeaders:KnownProxies:0"] = TrustedProxy,
            });
        });
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, ClientIpFromHeaderStartupFilter>());
    }
}

/// <summary>
/// Test-only. Sets the TCP peer before forwarded headers run. Production partitions on the
/// resolved client IP, which this host cannot vary per request.
/// </summary>
internal sealed class ClientIpFromHeaderStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var raw)
                && System.Net.IPAddress.TryParse(raw.ToString(), out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            await nextMiddleware(context);
        });
        next(app);
    };
}

public class EdgeProxyContractTests
{
    private const string EdgeIp = "172.30.20.10";

    [Fact]
    public void Nginx_OverwritesForwardedForWithThePeer_AndDropsClientCfConnectingIp()
    {
        var conf = File.ReadAllText(Path.Combine(RepoRoot(), "deploy/nginx/default.conf"));
        var directives = conf.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        directives.Should().Contain("proxy_set_header X-Forwarded-For $remote_addr;");
        directives.Should().Contain("proxy_set_header CF-Connecting-IP \"\";");
        directives.Should().Contain("set_real_ip_from 172.30.20.3;");
        directives.Should().Contain("real_ip_header X-Forwarded-For;");
        directives.Should().NotContain(line =>
            line.Contains("$http_x_forwarded_for", StringComparison.Ordinal)
            || line.Contains("$proxy_add_x_forwarded_for", StringComparison.Ordinal)
            || line.Contains("$http_cf_connecting_ip", StringComparison.Ordinal)
            || line.Contains("wl_forwarded_for", StringComparison.Ordinal)
            || line.Contains("172.30.20.0/24", StringComparison.Ordinal));
    }

    [Fact]
    public void WebNginx_ForwardsThePeerToTheEdge_AndDropsClientCfConnectingIp()
    {
        var conf = File.ReadAllText(Path.Combine(RepoRoot(), "deploy/nginx/web.conf"));
        var directives = conf.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        directives.Should().Contain("proxy_pass http://172.30.20.10:8080;");
        directives.Should().Contain("proxy_set_header X-Forwarded-For $remote_addr;");
        directives.Should().Contain("proxy_set_header CF-Connecting-IP \"\";");
        directives.Should().NotContain(line =>
            line.Contains("api:8080", StringComparison.Ordinal)
            || line.Contains("$http_x_forwarded_for", StringComparison.Ordinal)
            || line.Contains("$proxy_add_x_forwarded_for", StringComparison.Ordinal)
            || line.Contains("$http_cf_connecting_ip", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposeEdgeAddress_MatchesKnownProxies_AndWebNginxIsPinnedApartFromIt()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        var edgeConf = File.ReadAllText(Path.Combine(RepoRoot(), "deploy/nginx/default.conf"));
        var webConf = File.ReadAllText(Path.Combine(RepoRoot(), "deploy/nginx/web.conf"));

        var proxies = System.Text.RegularExpressions.Regex.Matches(
            compose,
            "ForwardedHeaders__KnownProxies__\\d+:\\s*\"([^\"]+)\"");
        proxies.Should().ContainSingle();
        var trusted = proxies[0].Groups[1].Value;

        var edgeIp = Ipv4ForService(compose, "nginx");
        var webIp = Ipv4ForService(compose, "web");
        edgeIp.Should().Be(trusted);
        webIp.Should().NotBe(trusted);

        var upstream = System.Text.RegularExpressions.Regex.Match(
            webConf,
            "proxy_pass http://([^:;]+):8080;");
        upstream.Success.Should().BeTrue();
        upstream.Groups[1].Value.Should().Be(trusted);

        var realIps = System.Text.RegularExpressions.Regex.Matches(
            edgeConf,
            @"set_real_ip_from\s+([^;]+);");
        realIps.Should().ContainSingle();
        realIps[0].Groups[1].Value.Trim().Should().Be(webIp);

        compose.Should().NotContain("ForwardedHeaders__KnownNetworks");
    }

    [Fact]
    public void Compose_TrustsOnlyTheFixedEdgeAddress()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        var trusted = Ipv4ForService(compose, "nginx");
        compose.Should().Contain($"ForwardedHeaders__KnownProxies__0: \"{trusted}\"");
        compose.Should().NotContain("ForwardedHeaders__KnownProxies__1");
        compose.Should().NotContain("ForwardedHeaders__KnownNetworks");
    }

    [Fact]
    public void WideKnownNetwork_IsRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = EdgeIp,
            ["ForwardedHeaders:KnownNetworks:0"] = "172.30.20.0/24",
        }).Build();
        var services = new ServiceCollection();
        services.AddWorklaneForwardedHeaders(config);
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        act.Should().Throw<InvalidOperationException>().WithMessage("*172.30.20.0/24*");
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate the repository root.");
        return root.FullName;
    }

    private static string Ipv4ForService(string compose, string service)
    {
        var lines = compose.Split('\n');
        var inService = false;
        string? ip = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                continue;

            if (System.Text.RegularExpressions.Regex.IsMatch(line, $"^  {service}:\\s*$"))
            {
                inService = true;
                ip = null;
                continue;
            }

            if (inService && System.Text.RegularExpressions.Regex.IsMatch(line, "^  [A-Za-z0-9_-]+:\\s*$"))
                break;

            if (!inService)
                continue;

            var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s+ipv4_address:\s*(""[^""]+""|\S+)\s*$");
            if (!match.Success)
                continue;

            ip = match.Groups[1].Value.Trim('"');
        }

        ip.Should().NotBeNull($"service {service} should pin an ipv4_address");
        return ip!;
    }
}
