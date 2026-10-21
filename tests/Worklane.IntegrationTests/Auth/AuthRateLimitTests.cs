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
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, blockedBody);
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        blocked.Headers.RetryAfter.Should().NotBeNull();
        blocked.Headers.RetryAfter!.Delta.Should().NotBeNull();
        blocked.Headers.RetryAfter.Delta!.Value.TotalSeconds.Should().BeGreaterThan(0);
        blockedBody.Should().NotContain("retryAfterSeconds");
        var problem = JsonDocument.Parse(blockedBody).RootElement;
        problem.GetProperty("code").GetString().Should().Be("rate_limited");
        problem.GetProperty("scope").GetString().Should().Be("ip");
        problem.GetProperty("detail").GetString().Should().Be("Too many requests.");
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();

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

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation(ClientIpFromHeaderStartupFilter.HeaderName, ip);
        return await client.SendAsync(request);
    }
}

public class AuthRateLimitFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Auth:PermitLimit"] = "3",
                ["RateLimit:Auth:WindowSeconds"] = "60",
            });
        });
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, ClientIpFromHeaderStartupFilter>());
    }
}

/// <summary>
/// Test-only. Production partitions on the connection address, which this host cannot vary per request.
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
