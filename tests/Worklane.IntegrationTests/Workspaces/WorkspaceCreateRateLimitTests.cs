using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Common.Options;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Auth;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class WorkspaceCreateRateLimitSettingsTests
{
    [Fact]
    public void Defaults_AreFivePerHour()
    {
        var settings = new WorkspaceCreateRateLimitSettings();
        settings.PermitLimit.Should().Be(5);
        settings.WindowSeconds.Should().Be(3600);
        WorkspaceCreateRateLimitSettings.PolicyName.Should().Be("workspace-create");
        WorkspaceCreateRateLimitSettings.SectionName.Should().Be("RateLimit:WorkspaceCreate");
    }

    [Fact]
    public void AppSettings_UseTheSameDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        root.Should().NotBeNull();

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "src/Worklane.WebApi/appsettings.json")));
        var section = json.RootElement.GetProperty("RateLimit").GetProperty("WorkspaceCreate");
        section.GetProperty("PermitLimit").GetInt32().Should().Be(5);
        section.GetProperty("WindowSeconds").GetInt32().Should().Be(3600);
    }
}

public class WorkspaceCreateRateLimitTests : IClassFixture<WorkspaceCreateRateLimitFactory>
{
    private readonly WorkspaceCreateRateLimitFactory _factory;

    public WorkspaceCreateRateLimitTests(WorkspaceCreateRateLimitFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnderTheLimit_Returns200_OverTheLimit_Returns429()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit; i++)
        {
            var created = await CreateAsync(user.Client);
            var body = await created.Content.ReadAsStringAsync();
            // Create stays 200 OK. The limiter does not change that status.
            created.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var workspace = JsonSerializer.Deserialize<WorkspaceDto>(body, TestAuthHelper.JsonOptions);
            workspace!.MyRole.Should().Be(WorkspaceRole.Owner);
            workspace.Name.Should().NotBeNullOrWhiteSpace();
        }

        var blocked = await CreateAsync(user.Client);
        await AssertRateLimited(blocked);

        var again = await CreateAsync(user.Client);
        await AssertRateLimited(again);
    }

    [Fact]
    public async Task SeparateUsers_HaveSeparateBuckets_EvenFromTheSameClientAddress()
    {
        var first = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var sharedForwardedFor = "203.0.113.40";

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit; i++)
        {
            var created = await CreateAsync(first.Client, peer: WorkspaceCreateRateLimitFactory.TrustedProxy, forwardedFor: sharedForwardedFor);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        var blocked = await CreateAsync(first.Client, peer: WorkspaceCreateRateLimitFactory.TrustedProxy, forwardedFor: "203.0.113.41");
        await AssertRateLimited(blocked);

        var spoofed = await CreateAsync(first.Client, peer: "198.51.100.20", forwardedFor: "203.0.113.99");
        await AssertRateLimited(spoofed);

        var otherUser = await CreateAsync(second.Client, peer: WorkspaceCreateRateLimitFactory.TrustedProxy, forwardedFor: sharedForwardedFor);
        otherUser.StatusCode.Should().Be(HttpStatusCode.OK, await otherUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TwoClients_WithTheSameUser_ShareOneBucket()
    {
        var first = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = _factory.CreateClient();
        second.DefaultRequestHeaders.Authorization = first.Client.DefaultRequestHeaders.Authorization;

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit - 1; i++)
        {
            (await CreateAsync(first.Client)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await CreateAsync(second)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertRateLimited(await CreateAsync(second));
        await AssertRateLimited(await CreateAsync(first.Client));
    }

    [Fact]
    public async Task Unauthenticated_Stays401_AndDoesNotSpendAUserBucket()
    {
        var anonymous = _factory.CreateClient();
        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit + 2; i++)
        {
            var denied = await anonymous.PostAsJsonAsync("/api/workspaces", new { name = "Secret " + i });
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            denied.Headers.RetryAfter.Should().BeNull();
        }

        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit; i++)
        {
            (await CreateAsync(user.Client)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task OtherEndpoints_StayAvailable_AfterCreateIsLimited()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var invitee = $"invite-{Guid.NewGuid():N}@example.com";

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit; i++)
            (await CreateAsync(user.Client)).EnsureSuccessStatusCode();

        await AssertRateLimited(await CreateAsync(user.Client));

        var list = await user.Client.GetAsync("/api/workspaces");
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var invite = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/invitations",
            new InviteMemberRequest(invitee, WorkspaceRole.Member));
        invite.StatusCode.Should().Be(HttpStatusCode.OK, await invite.Content.ReadAsStringAsync());

        var project = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new { name = "Still open", key = "SO" });
        project.StatusCode.Should().Be(HttpStatusCode.OK, await project.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConcurrentCreates_AdmitOnlyThePermitLimit_PerUser()
    {
        var first = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        const int burst = 8;

        var firstBurst = Task.WhenAll(Enumerable.Range(0, burst).Select(_ => CreateAsync(first.Client)));
        var secondBurst = Task.WhenAll(Enumerable.Range(0, burst).Select(_ => CreateAsync(second.Client)));
        var results = await Task.WhenAll(firstBurst, secondBurst);

        await AssertBurst(results[0]);
        await AssertBurst(results[1]);
    }

    private static async Task AssertBurst(HttpResponseMessage[] responses)
    {
        var statuses = new List<HttpStatusCode>(responses.Length);
        foreach (var response in responses)
        {
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                await AssertRateLimited(response);
            else
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        statuses.Count(status => status == HttpStatusCode.OK)
            .Should().Be(WorkspaceCreateRateLimitFactory.PermitLimit);
        statuses.Count(status => status == HttpStatusCode.TooManyRequests)
            .Should().Be(responses.Length - WorkspaceCreateRateLimitFactory.PermitLimit);
    }

    private static async Task AssertRateLimited(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.RetryAfter!.Delta.Should().NotBeNull();
        var seconds = response.Headers.RetryAfter.Delta!.Value.TotalSeconds;
        seconds.Should().BeGreaterThan(0);
        seconds.Should().BeLessThanOrEqualTo(WorkspaceCreateRateLimitFactory.WindowSeconds);
        body.Should().NotContain("retryAfterSeconds");
        body.Should().NotContain("PermitLimit");
        body.Should().NotContain("WindowSeconds");

        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("rate_limited");
        problem.GetProperty("scope").GetString().Should().Be("user");
        problem.GetProperty("detail").GetString().Should().Be("Too many requests.");
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        problem.TryGetProperty("permitLimit", out _).Should().BeFalse();
        problem.TryGetProperty("limit", out _).Should().BeFalse();
        problem.TryGetProperty("remaining", out _).Should().BeFalse();
        foreach (var property in problem.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number)
                continue;
            property.Value.GetInt32().Should().NotBe(WorkspaceCreateRateLimitFactory.PermitLimit);
            property.Value.GetInt32().Should().NotBe(WorkspaceCreateRateLimitFactory.WindowSeconds);
        }
    }

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        string? peer = null,
        string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces")
        {
            Content = JsonContent.Create(new { name = "ws-" + Guid.NewGuid().ToString("N") })
        };
        if (peer is not null)
            request.Headers.TryAddWithoutValidation(ClientIpFromHeaderStartupFilter.HeaderName, peer);
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }
}

public class WorkspaceCreateRateLimitFactory : WorklaneWebApplicationFactory
{
    public const int PermitLimit = 3;

    public const int WindowSeconds = 60;

    public const string TrustedProxy = "10.50.0.1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:WorkspaceCreate:PermitLimit"] = PermitLimit.ToString(),
                ["RateLimit:WorkspaceCreate:WindowSeconds"] = WindowSeconds.ToString(),
                ["ForwardedHeaders:KnownProxies:0"] = TrustedProxy,
            });
        });
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, ClientIpFromHeaderStartupFilter>());
    }
}
