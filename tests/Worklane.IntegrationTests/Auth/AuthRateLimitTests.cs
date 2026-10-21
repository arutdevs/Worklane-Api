using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Worklane.IntegrationTests.Infrastructure;

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
            .StatusCode.Should().Be(HttpStatusCode.OK);
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
    public async Task ClientsBehindTrustedNetwork_WithDifferentCfConnectingIp_GetSeparateBuckets()
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
                AuthRateLimitFactory.TrustedNetworkPeer,
                sharedForwardedFor,
                first);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedNetworkPeer,
            sharedForwardedFor,
            first);
        await AssertRateLimited(blocked);

        var otherClient = await Post(
            client,
            "/api/auth/login",
            new { email = "nobody@example.com", password = "WrongPassword1" },
            AuthRateLimitFactory.TrustedNetworkPeer,
            sharedForwardedFor,
            second);
        otherClient.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
    public const string TrustedNetworkPeer = "10.60.0.8";

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
                ["ForwardedHeaders:KnownNetworks:0"] = "10.60.0.0/24",
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
