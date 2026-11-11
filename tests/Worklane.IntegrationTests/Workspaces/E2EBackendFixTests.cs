using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class E2EBackendFixTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public E2EBackendFixTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Invite_ReturnsConfiguredLink_AndDuplicateMemberIs409()
    {
        var owner = await RegisterAsync($"owner-invite-{Guid.NewGuid():N}@example.com", "Owner");
        var member = await RegisterAsync($"member-invite-{Guid.NewGuid():N}@example.com", "Member");

        var added = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(member.Tokens.User.Email, WorkspaceRole.Member));
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var addedBody = await added.Content.ReadFromJsonAsync<InvitationDto>(TestAuthHelper.JsonOptions);
        addedBody!.InviteLink.Should().BeNull();

        var again = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(member.Tokens.User.Email, WorkspaceRole.Member));
        await AssertConflict(again, "workspace_member_exists", "This user is already a member of the workspace.");

        var pendingEmail = $"pending-{Guid.NewGuid():N}@example.com";
        var pending = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(pendingEmail, WorkspaceRole.Member));
        var pendingText = await pending.Content.ReadAsStringAsync();
        pending.StatusCode.Should().Be(HttpStatusCode.OK, pendingText);
        var invitation = JsonSerializer.Deserialize<InvitationDto>(pendingText, TestAuthHelper.JsonOptions);
        invitation!.InviteLink.Should().StartWith("http://web.test/invitations/accept?token=");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var stored = await db.Invitations.IgnoreQueryFilters().SingleAsync(row => row.Email == pendingEmail);
        const string marker = "token=";
        var tokenStart = invitation.InviteLink!.IndexOf(marker, StringComparison.Ordinal);
        tokenStart.Should().BeGreaterThanOrEqualTo(0);
        var token = invitation.InviteLink[(tokenStart + marker.Length)..];
        invitation.InviteLink.Should().Be(InviteLinks.ForToken("http://web.test", token));
        stored.TokenHash.Should().Be(InviteTokens.Hash(token));
        stored.TokenHash.Should().NotBe(token);
    }

    [Fact]
    public async Task CreateWorkspace_RequiresAuth_MakesTheCallerOwner_AndKeepsTheSlugUnique()
    {
        var anonymous = _factory.CreateClient();
        var denied = await anonymous.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("Secret"));
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = await RegisterAsync($"ws-{Guid.NewGuid():N}@example.com", "Owner");
        var outsider = await RegisterAsync($"ws-out-{Guid.NewGuid():N}@example.com", "Outsider");

        var blank = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("   "));
        await AssertValidation(blank, "Name", "not_empty");

        const string name = "สำนักงาน";
        var created = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name));
        var createdText = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK, createdText);
        var workspace = JsonSerializer.Deserialize<WorkspaceDto>(createdText, TestAuthHelper.JsonOptions);
        workspace!.Name.Should().Be(name);
        workspace.Slug.Should().Be(name);
        workspace.Slug.Should().Contain("\u0E33");
        workspace.MyRole.Should().Be(WorkspaceRole.Owner);

        var second = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name));
        second.EnsureSuccessStatusCode();
        var again = await second.Content.ReadFromJsonAsync<WorkspaceDto>(TestAuthHelper.JsonOptions);
        again!.Slug.Should().Be(name + "-1");
        again.MyRole.Should().Be(WorkspaceRole.Owner);
        again.Id.Should().NotBe(workspace.Id);

        var mine = await user.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        mine!.Select(item => item.Id).Should().Contain([workspace.Id, again.Id]);

        var theirs = await outsider.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        theirs!.Select(item => item.Id).Should().NotContain([workspace.Id, again.Id]);
    }

    [Fact]
    public async Task CreateWorkspace_ConcurrentSameName_RetriesTheSlug()
    {
        var user = await RegisterAsync($"ws-race-{Guid.NewGuid():N}@example.com", "Slug");
        var name = "race" + Guid.NewGuid().ToString("N");
        var gate = _factory.Services.GetRequiredService<WorkspaceInsertGate>();
        gate.Arm(2);
        HttpResponseMessage[] pending;
        try
        {
            pending = await Task.WhenAll(
                user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name)),
                user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name)));
        }
        finally
        {
            gate.Disarm();
        }

        var bodies = new string[pending.Length];
        for (var i = 0; i < pending.Length; i++)
        {
            bodies[i] = await pending[i].Content.ReadAsStringAsync();
            pending[i].StatusCode.Should().Be(HttpStatusCode.OK, bodies[i]);
        }

        var slugs = bodies
            .Select(body => JsonDocument.Parse(body).RootElement.GetProperty("slug").GetString())
            .ToArray();
        slugs.Should().BeEquivalentTo(name, name + "-1");
        gate.Arrivals.Should().BeGreaterThanOrEqualTo(2);
    }

    private async Task<RegisteredUser> RegisterAsync(
        string email,
        string displayName,
        string? preferredLanguage = null,
        string? acceptLanguage = null)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new RegisterRequest(email, "Password123!", displayName, preferredLanguage))
        };
        if (!string.IsNullOrEmpty(acceptLanguage))
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Register did not return tokens.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var workspaces = await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Could not read workspaces.");
        var workspace = workspaces.Single(item => item.Name == WorkspaceLocale.WorkspaceName(displayName, tokens.User.PreferredLanguage));
        return new RegisteredUser(client, tokens, workspace);
    }

    private static async Task AssertConflict(HttpResponseMessage response, string code, string detail)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be(code);
        doc.RootElement.GetProperty("detail").GetString().Should().Be(detail);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        text.Should().NotContain("23505");
        text.Should().NotContain("IX_");
    }

    private static async Task AssertValidation(HttpResponseMessage response, string field, string fieldCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        doc.RootElement.GetProperty("fieldCodes").GetProperty(field).EnumerateArray().Select(item => item.GetString())
            .Should().Contain(fieldCode);
    }
}
