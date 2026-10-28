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

public class WorkspaceCreationSettingsTests
{
    [Fact]
    public void Defaults_AreTenPerHour_AndTwentyOwned()
    {
        var settings = new WorkspaceCreationSettings();
        settings.PermitLimit.Should().Be(10);
        settings.WindowSeconds.Should().Be(3600);
        settings.MaxOwnedWorkspaces.Should().Be(20);
        WorkspaceCreationSettings.PolicyName.Should().Be("workspace-create");
        WorkspaceCreationSettings.SectionName.Should().Be("WorkspaceCreation");
    }

    [Fact]
    public void AppSettings_UseTheSameDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        root.Should().NotBeNull();

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "src/Worklane.WebApi/appsettings.json")));
        var section = json.RootElement.GetProperty("WorkspaceCreation");
        section.GetProperty("PermitLimit").GetInt32().Should().Be(10);
        section.GetProperty("WindowSeconds").GetInt32().Should().Be(3600);
        section.GetProperty("MaxOwnedWorkspaces").GetInt32().Should().Be(20);
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
    public async Task RequestPastThePermit_Is429_WithRetryAfter_AndScopeUser()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit; i++)
        {
            var created = await CreateAsync(user.Client);
            var body = await created.Content.ReadAsStringAsync();
            created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        }

        await AssertRateLimited(await CreateAsync(user.Client));
    }

    [Fact]
    public async Task AnotherUser_IsNotAffected_ByTheSameClientAddress()
    {
        var first = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        const string sharedForwardedFor = "203.0.113.40";

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit; i++)
        {
            var created = await CreateAsync(
                first.Client,
                peer: WorkspaceCreateRateLimitFactory.TrustedProxy,
                forwardedFor: sharedForwardedFor);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        await AssertRateLimited(await CreateAsync(
            first.Client,
            peer: WorkspaceCreateRateLimitFactory.TrustedProxy,
            forwardedFor: "203.0.113.41"));
        await AssertRateLimited(await CreateAsync(
            first.Client,
            peer: "198.51.100.20",
            forwardedFor: "203.0.113.99"));

        var other = await CreateAsync(
            second.Client,
            peer: WorkspaceCreateRateLimitFactory.TrustedProxy,
            forwardedFor: sharedForwardedFor);
        other.StatusCode.Should().Be(HttpStatusCode.OK, await other.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RejectionOrder_Is401_Then429_Before400()
    {
        var anonymous = _factory.CreateClient();
        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit + 2; i++)
        {
            var denied = await anonymous.PostAsJsonAsync("/api/workspaces", new { name = "   " });
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            denied.Headers.RetryAfter.Should().BeNull();
        }

        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var invalid = await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "   " });
        await AssertValidation(invalid);

        for (var i = 0; i < WorkspaceCreateRateLimitFactory.PermitLimit - 1; i++)
            (await CreateAsync(user.Client)).StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "   " });
        await AssertRateLimited(blocked);
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
        problem.GetProperty("scope").GetString().Should().Be("user");
        problem.GetProperty("detail").GetString().Should().Be("Too many requests.");
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        problem.TryGetProperty("limit", out _).Should().BeFalse();
    }

    private static async Task AssertValidation(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
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
    public const int PermitLimit = 2;

    public const string TrustedProxy = "10.50.0.1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceCreation:PermitLimit"] = PermitLimit.ToString(),
                ["WorkspaceCreation:WindowSeconds"] = "60",
                ["WorkspaceCreation:MaxOwnedWorkspaces"] = "100",
                ["ForwardedHeaders:KnownProxies:0"] = TrustedProxy,
            });
        });
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, ClientIpFromHeaderStartupFilter>());
    }
}

public class WorkspaceOwnershipCapTests : IClassFixture<WorkspaceOwnershipCapFactory>
{
    private readonly WorkspaceOwnershipCapFactory _factory;

    public WorkspaceOwnershipCapTests(WorkspaceOwnershipCapFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AtTheOwnershipCap_Is409_WithLimit_AndInvalidBodyStays400()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 1; i < WorkspaceOwnershipCapFactory.MaxOwned; i++)
        {
            var created = await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "owned-" + Guid.NewGuid().ToString("N") });
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        var invalid = await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "   " });
        var invalidBody = await invalid.Content.ReadAsStringAsync();
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, invalidBody);
        JsonDocument.Parse(invalidBody).RootElement.GetProperty("code").GetString().Should().Be("validation_failed");

        var blocked = await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "one-too-many" });
        await AssertOwnershipCap(blocked, WorkspaceOwnershipCapFactory.MaxOwned);
    }

    [Fact]
    public async Task MembershipThatIsNotOwner_DoesNotCountTowardTheCap()
    {
        var host = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var otherHost = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        foreach (var owner in new[] { host, otherHost })
        {
            var invite = await owner.Client.PostAsJsonAsync(
                $"/api/workspaces/{owner.Workspace.Id}/invitations",
                new InviteMemberRequest(member.Tokens.User.Email, WorkspaceRole.Member));
            invite.StatusCode.Should().Be(HttpStatusCode.OK, await invite.Content.ReadAsStringAsync());
        }

        var created = await member.Client.PostAsJsonAsync("/api/workspaces", new { name = "still-allowed-" + Guid.NewGuid().ToString("N") });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var blocked = await member.Client.PostAsJsonAsync("/api/workspaces", new { name = "over-owned-" + Guid.NewGuid().ToString("N") });
        await AssertOwnershipCap(blocked, WorkspaceOwnershipCapFactory.MaxOwned);
    }

    private static async Task AssertOwnershipCap(HttpResponseMessage response, int limit)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("workspace_limit_reached");
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("limit").GetInt32().Should().Be(limit);
        problem.GetProperty("detail").GetString().Should().Be($"You can own at most {limit} workspaces.");
    }
}

public class WorkspaceOwnershipCapFactory : WorklaneWebApplicationFactory
{
    public const int MaxOwned = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceCreation:PermitLimit"] = "100",
                ["WorkspaceCreation:WindowSeconds"] = "60",
                ["WorkspaceCreation:MaxOwnedWorkspaces"] = MaxOwned.ToString(),
            });
        });
    }
}

public class WorkspaceRegistrationCapTests : IClassFixture<WorkspaceRegistrationCapFactory>
{
    private readonly WorkspaceRegistrationCapFactory _factory;

    public WorkspaceRegistrationCapTests(WorkspaceRegistrationCapFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Registration_StillCreatesThePersonalWorkspace_WhenTheCapIsAlreadyZero()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        user.Workspace.MyRole.Should().Be(WorkspaceRole.Owner);

        var mine = await user.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        mine.Should().ContainSingle();
        mine![0].MyRole.Should().Be(WorkspaceRole.Owner);

        var blocked = await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "no-more" });
        var body = await blocked.Content.ReadAsStringAsync();
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("workspace_limit_reached");
        problem.GetProperty("limit").GetInt32().Should().Be(0);
        problem.GetProperty("detail").GetString().Should().Be("You can own at most 0 workspaces.");
    }
}

public class WorkspaceRegistrationCapFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceCreation:PermitLimit"] = "100",
                ["WorkspaceCreation:WindowSeconds"] = "60",
                ["WorkspaceCreation:MaxOwnedWorkspaces"] = "0",
            });
        });
    }
}
