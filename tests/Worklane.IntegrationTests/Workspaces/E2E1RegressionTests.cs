using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Common.Options;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

/// <summary>
/// API-level regression for the E2E1 flows QA signed off on develop.
/// Test-only: these cases drive the public HTTP API and do not change production code.
/// </summary>
public class E2E1RegressionTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private const string Password = "Password123!";

    private readonly WorklaneWebApplicationFactory _factory;

    public E2E1RegressionTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateWorkspace_EmptyName_IsValidationError()
    {
        var user = await RegisterAsync($"e2e-ws-{Guid.NewGuid():N}@example.com", "Workspace User");

        foreach (var name in new[] { "", "   " })
        {
            var response = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name));
            await AssertValidation(response, "Name", "not_empty");
        }

        var workspaces = await user.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        workspaces.Should().ContainSingle().Which.Id.Should().Be(user.Workspace.Id);
    }

    [Fact]
    public async Task Invite_OwnerLinkUsesFrontendOrigin_InviteeJoinsAndReadsBoard_ReuseIsNotFound_MemberIsForbidden()
    {
        var owner = await RegisterAsync($"e2e-owner-{Guid.NewGuid():N}@example.com", "Owner");
        var project = await CreateProjectAsync(owner, "Invite Board", "INV");
        var pendingEmail = $"e2e-invitee-{Guid.NewGuid():N}@example.com";

        var invite = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(pendingEmail, WorkspaceRole.Member));
        var inviteText = await invite.Content.ReadAsStringAsync();
        invite.StatusCode.Should().Be(HttpStatusCode.OK, inviteText);

        using var inviteJson = JsonDocument.Parse(inviteText);
        inviteJson.RootElement.TryGetProperty("inviteLink", out var linkProperty).Should().BeTrue();
        linkProperty.ValueKind.Should().Be(JsonValueKind.String);
        var inviteLink = linkProperty.GetString();
        inviteLink.Should().NotBeNullOrWhiteSpace();

        var frontend = _factory.Services.GetRequiredService<IOptions<FrontendSettings>>().Value.BaseUrl.Trim().TrimEnd('/');
        var link = new Uri(inviteLink!, UriKind.Absolute);
        link.GetLeftPart(UriPartial.Authority).Should().Be(frontend);
        link.AbsolutePath.Should().Be("/invitations/accept");
        var token = QueryHelpers.ParseQuery(link.Query)["token"].ToString();
        token.Should().NotBeNullOrWhiteSpace();

        var invitee = await RegisterAsync(pendingEmail, "Invitee");
        var accept = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var members = await invitee.Client.GetFromJsonAsync<List<WorkspaceMemberDto>>(
            $"/api/workspaces/{owner.Workspace.Id}/members",
            TestAuthHelper.JsonOptions);
        members.Should().Contain(member => member.UserId == invitee.Tokens.User.Id && member.Role == WorkspaceRole.Member);

        var projects = await invitee.Client.GetFromJsonAsync<List<ProjectDto>>(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            TestAuthHelper.JsonOptions);
        projects.Should().ContainSingle().Which.Id.Should().Be(project.Id);
        var columns = await GetColumnsAsync(invitee.Client, project.Id);
        columns.Should().NotBeEmpty();

        var reuse = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        await AssertProblem(reuse, HttpStatusCode.NotFound, "not_found");

        var stranger = await RegisterAsync($"e2e-stranger-{Guid.NewGuid():N}@example.com", "Stranger");
        var strangerReuse = await stranger.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        await AssertProblem(strangerReuse, HttpStatusCode.NotFound, "not_found");

        var unknown = await stranger.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(Convert.ToHexString(Guid.NewGuid().ToByteArray())));
        await AssertProblem(unknown, HttpStatusCode.NotFound, "not_found");

        var memberOnly = await RegisterAsync($"e2e-member-{Guid.NewGuid():N}@example.com", "Member");
        var addMember = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(memberOnly.Tokens.User.Email, WorkspaceRole.Member));
        addMember.StatusCode.Should().Be(HttpStatusCode.OK);

        var memberInvite = await memberOnly.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest($"e2e-denied-{Guid.NewGuid():N}@example.com", WorkspaceRole.Member));
        await AssertProblem(memberInvite, HttpStatusCode.Forbidden, "workspace_access_denied");
    }

    [Fact]
    public async Task Invite_AnotherUsersLiveToken_IsNotFound()
    {
        var owner = await RegisterAsync($"e2e-live-owner-{Guid.NewGuid():N}@example.com", "Owner");
        var pendingEmail = $"e2e-live-invitee-{Guid.NewGuid():N}@example.com";
        var token = await InvitePendingTokenAsync(owner, pendingEmail);

        var other = await RegisterAsync($"e2e-live-other-{Guid.NewGuid():N}@example.com", "Other");
        var response = await other.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        var unknown = await other.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(Convert.ToHexString(Guid.NewGuid().ToByteArray())));
        var responseBody = await response.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, responseBody);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, unknownBody);
        responseBody.Should().Be(unknownBody);

        var invitee = await RegisterAsync(pendingEmail, "Invitee");
        var accept = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<RegisteredUser> RegisterAsync(
        string email,
        string displayName,
        string? preferredLanguage = null)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, displayName, preferredLanguage));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Register did not return tokens.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var workspaces = await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Could not read workspaces.");
        workspaces.Should().ContainSingle();
        workspaces[0].MyRole.Should().Be(WorkspaceRole.Owner);
        return new RegisteredUser(client, tokens, workspaces[0]);
    }

    private static async Task<ProjectDto> CreateProjectAsync(RegisteredUser user, string name, string key, string? language = null)
    {
        var response = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new CreateProjectRequest(name, key, null, language));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<ProjectDto>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Project was not created.");
    }

    private static async Task<List<BoardColumnDto>> GetColumnsAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/projects/{projectId}/columns");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Columns were not returned.");
    }

    private static async Task<string> InvitePendingTokenAsync(RegisteredUser owner, string email)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, WorkspaceRole.Member));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        var link = new Uri(json.RootElement.GetProperty("inviteLink").GetString()!, UriKind.Absolute);
        return QueryHelpers.ParseQuery(link.Query)["token"].ToString();
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    private static async Task AssertValidation(HttpResponseMessage response, string field, string fieldCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        document.RootElement.GetProperty("title").GetString().Should().Be("Validation Failed");
        document.RootElement.GetProperty("fieldCodes").GetProperty(field).EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain(fieldCode);
    }
}
