using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class InviteCreationSettingsTests
{
    [Fact]
    public void Defaults_AreTwentyPerHour_AndFiftyPending()
    {
        var settings = new InviteCreationSettings();
        settings.PermitLimit.Should().Be(20);
        settings.WindowSeconds.Should().Be(3600);
        settings.MaxPendingInvites.Should().Be(50);
        InviteCreationSettings.PolicyName.Should().Be("invite-create");
        InviteCreationSettings.SectionName.Should().Be("InviteCreation");
    }

    [Fact]
    public void AppSettings_UseTheSameDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        root.Should().NotBeNull();

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "src/Worklane.WebApi/appsettings.json")));
        var section = json.RootElement.GetProperty("InviteCreation");
        section.GetProperty("PermitLimit").GetInt32().Should().Be(20);
        section.GetProperty("WindowSeconds").GetInt32().Should().Be(3600);
        section.GetProperty("MaxPendingInvites").GetInt32().Should().Be(50);
    }
}

public class InviteRateLimitTests : IClassFixture<InviteRateLimitFactory>
{
    private readonly InviteRateLimitFactory _factory;

    public InviteRateLimitTests(InviteRateLimitFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TwentyFirstInviteWithinTheHour_Is429_WithInviteRateLimited()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 0; i < InviteRateLimitFactory.PermitLimit; i++)
        {
            var created = await InviteAsync(user.Client, user.Workspace.Id);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        await AssertRateLimited(await InviteAsync(user.Client, user.Workspace.Id));
    }

    [Fact]
    public async Task Limit_IsPerUser_AnotherUserIsNotBlocked()
    {
        var first = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 0; i < InviteRateLimitFactory.PermitLimit; i++)
        {
            var created = await InviteAsync(first.Client, first.Workspace.Id);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        await AssertRateLimited(await InviteAsync(first.Client, first.Workspace.Id));

        var createdWorkspace = await first.Client.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "other-" + Guid.NewGuid().ToString("N") });
        createdWorkspace.StatusCode.Should().Be(HttpStatusCode.OK, await createdWorkspace.Content.ReadAsStringAsync());
        var other = await createdWorkspace.Content.ReadFromJsonAsync<WorkspaceDto>(TestAuthHelper.JsonOptions);
        var otherId = other?.Id ?? throw new InvalidOperationException("The second workspace was not created.");
        await AssertRateLimited(await InviteAsync(first.Client, otherId));

        var allowed = await InviteAsync(second.Client, second.Workspace.Id);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
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
        problem.GetProperty("code").GetString().Should().Be("invite_rate_limited");
        problem.GetProperty("scope").GetString().Should().Be("user");
        problem.GetProperty("detail").GetString().Should().Be("Too many requests.");
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("title").GetString().Should().Be("Too Many Requests");
        problem.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        problem.TryGetProperty("limit", out _).Should().BeFalse();
    }

    private static Task<HttpResponseMessage> InviteAsync(HttpClient client, Guid workspaceId) =>
        client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/invitations",
            new InviteMemberRequest($"invite-{Guid.NewGuid():N}@example.com", WorkspaceRole.Member));
}

public class InviteRateLimitFactory : WorklaneWebApplicationFactory
{
    public const int PermitLimit = 20;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InviteCreation:PermitLimit"] = PermitLimit.ToString(),
                ["InviteCreation:WindowSeconds"] = "3600",
                ["InviteCreation:MaxPendingInvites"] = "1000",
                ["WorkspaceCreation:PermitLimit"] = "100",
                ["WorkspaceCreation:MaxOwnedWorkspaces"] = "100",
            });
        });
        builder.ConfigureTestServices(services => services.DiscardInviteEmail());
    }
}

public class InvitePendingCapTests : IClassFixture<InvitePendingCapFactory>
{
    private readonly InvitePendingCapFactory _factory;

    public InvitePendingCapTests(InvitePendingCapFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AtThePendingCap_Is409_WithInvitePendingLimit()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 0; i < InvitePendingCapFactory.MaxPending; i++)
        {
            var created = await InviteAsync(owner.Client, owner.Workspace.Id);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        await AssertPendingCap(await InviteAsync(owner.Client, owner.Workspace.Id), InvitePendingCapFactory.MaxPending);
    }

    [Fact]
    public async Task ExpiredAndAcceptedInvites_DoNotCountTowardTheCap()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < InvitePendingCapFactory.MaxPending + 1; i++)
        {
            await SeedAsync(owner, acceptedAt: null, expiresAt: now.AddMinutes(-5));
            await SeedAsync(owner, acceptedAt: now.AddHours(-1), expiresAt: now.AddDays(3));
            await SeedAsync(owner, acceptedAt: now.AddDays(-2), expiresAt: now.AddDays(-1));
        }

        for (var i = 0; i < InvitePendingCapFactory.MaxPending; i++)
        {
            var created = await InviteAsync(owner.Client, owner.Workspace.Id);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        await AssertPendingCap(await InviteAsync(owner.Client, owner.Workspace.Id), InvitePendingCapFactory.MaxPending);
    }

    [Fact]
    public async Task Cap_IsPerWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        for (var i = 0; i < InvitePendingCapFactory.MaxPending; i++)
        {
            var created = await InviteAsync(owner.Client, owner.Workspace.Id);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        }

        await AssertPendingCap(await InviteAsync(owner.Client, owner.Workspace.Id), InvitePendingCapFactory.MaxPending);

        var createdWorkspace = await owner.Client.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "other-" + Guid.NewGuid().ToString("N") });
        createdWorkspace.StatusCode.Should().Be(HttpStatusCode.OK, await createdWorkspace.Content.ReadAsStringAsync());
        var other = await createdWorkspace.Content.ReadFromJsonAsync<WorkspaceDto>(TestAuthHelper.JsonOptions);
        var otherId = other?.Id ?? throw new InvalidOperationException("The second workspace was not created.");

        var allowed = await InviteAsync(owner.Client, otherId);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());

        await AssertPendingCap(await InviteAsync(owner.Client, owner.Workspace.Id), InvitePendingCapFactory.MaxPending);
    }

    private async Task SeedAsync(RegisteredUser owner, DateTimeOffset? acceptedAt, DateTimeOffset expiresAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        db.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = owner.Workspace.Id,
            Email = $"seed-{Guid.NewGuid():N}@example.com",
            Role = WorkspaceRole.Member,
            TokenHash = InviteTokens.Hash(Guid.NewGuid().ToString("N")),
            InvitedById = owner.Tokens.User.Id,
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            AcceptedAt = acceptedAt
        });
        await db.SaveChangesAsync();
    }

    private static async Task AssertPendingCap(HttpResponseMessage response, int limit)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("invite_pending_limit");
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Be("Conflict");
        problem.GetProperty("limit").GetInt32().Should().Be(limit);
        problem.GetProperty("detail").GetString().Should().Be(
            $"This workspace already has the maximum of {limit} pending invitations.");
    }

    private static Task<HttpResponseMessage> InviteAsync(HttpClient client, Guid workspaceId) =>
        client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/invitations",
            new InviteMemberRequest($"invite-{Guid.NewGuid():N}@example.com", WorkspaceRole.Member));
}

public class InvitePendingCapFactory : WorklaneWebApplicationFactory
{
    public const int MaxPending = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InviteCreation:PermitLimit"] = "1000",
                ["InviteCreation:WindowSeconds"] = "3600",
                ["InviteCreation:MaxPendingInvites"] = MaxPending.ToString(),
                ["WorkspaceCreation:PermitLimit"] = "100",
                ["WorkspaceCreation:MaxOwnedWorkspaces"] = "100",
            });
        });
        builder.ConfigureTestServices(services => services.DiscardInviteEmail());
    }
}

file static class InviteLimitEmail
{
    public static void DiscardInviteEmail(this IServiceCollection services)
    {
        services.RemoveAll<IEmailService>();
        services.AddSingleton<IEmailService, DiscardingEmailService>();
    }

    private sealed class DiscardingEmailService : IEmailService
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
