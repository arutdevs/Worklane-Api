using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class WorkspaceEndpointFailureTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public WorkspaceEndpointFailureTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NonMember_GetsTheSameNotFoundBody_AsAMissingWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var missing = Guid.NewGuid();
        var target = owner.Tokens.User.Id;
        var invite = new InviteMemberRequest($"pending-{Guid.NewGuid():N}@example.com", WorkspaceRole.Member);

        var cases = new (string Name, Func<Guid, Task<HttpResponseMessage>> Send)[]
        {
            ("GET members", id => outsider.Client.GetAsync($"/api/workspaces/{id}/members")),
            ("POST invitations", id => outsider.Client.PostAsJsonAsync($"/api/workspaces/{id}/invitations", invite)),
            ("PATCH role", id => outsider.Client.PatchAsJsonAsync($"/api/workspaces/{id}/members/{target}", new UpdateMemberRoleRequest(WorkspaceRole.Member))),
            ("DELETE member", id => outsider.Client.DeleteAsync($"/api/workspaces/{id}/members/{target}")),
        };

        foreach (var (name, send) in cases)
        {
            var hidden = await send(owner.Workspace.Id);
            var absent = await SendAs(owner, name, missing, target, invite);
            var hiddenBody = await hidden.Content.ReadAsStringAsync();
            var absentBody = await absent.Content.ReadAsStringAsync();

            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{name}: {hiddenBody}");
            absent.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{name} missing: {absentBody}");
            Normalize(hiddenBody, owner.Workspace.Id).Should().Be(Normalize(absentBody, missing), name);
            hiddenBody.Should().NotContain("workspace_access_denied");
            hiddenBody.Should().NotContain(owner.Tokens.User.Email);
            JsonDocument.Parse(hiddenBody).RootElement.GetProperty("code").GetString().Should().Be("not_found");
            JsonDocument.Parse(hiddenBody).RootElement.GetProperty("detail").GetString().Should().Be("Resource was not found.");
        }
    }

    [Fact]
    public async Task MemberWithTooLowARole_Gets403_OnInviteRoleAndRemove()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, viewer.Tokens.User.Email, WorkspaceRole.Viewer);
        await InviteAsync(owner, member.Tokens.User.Email, WorkspaceRole.Member);

        foreach (var caller in new[] { viewer, member })
        {
            await AssertAccessDenied(await caller.Client.PostAsJsonAsync(
                $"/api/workspaces/{owner.Workspace.Id}/invitations",
                new InviteMemberRequest($"x-{Guid.NewGuid():N}@example.com", WorkspaceRole.Viewer)));
            await AssertAccessDenied(await caller.Client.PatchAsJsonAsync(
                $"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}",
                new UpdateMemberRoleRequest(WorkspaceRole.Admin)));
            await AssertAccessDenied(await caller.Client.DeleteAsync(
                $"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}"));
        }

        (await viewer.Client.GetAsync($"/api/workspaces/{owner.Workspace.Id}/members"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.GetAsync($"/api/workspaces/{owner.Workspace.Id}/members"))
            .EnsureSuccessStatusCode();
        await AssertRole(owner, owner.Tokens.User.Id, WorkspaceRole.Owner);
    }

    [Fact]
    public async Task QueryFilters_FailClosed_AndDoNotReturnTheOtherWorkspace()
    {
        var alpha = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var beta = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var guest = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var pending = $"pending-{Guid.NewGuid():N}@example.com";
        await InviteAsync(alpha, pending, WorkspaceRole.Member);
        await InviteAsync(beta, guest.Tokens.User.Email, WorkspaceRole.Member);
        (await alpha.Client.PostAsJsonAsync(
            $"/api/workspaces/{alpha.Workspace.Id}/projects",
            new { name = "Alpha", key = "AL" })).EnsureSuccessStatusCode();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantService>();

        (await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == alpha.Workspace.Id || m.WorkspaceId == beta.Workspace.Id)).Should().Be(0);
        (await db.Invitations.CountAsync(i => i.Email == pending)).Should().Be(0);
        (await db.Projects.CountAsync(p => p.WorkspaceId == alpha.Workspace.Id)).Should().Be(0);
        (await db.WorkspaceMembershipEvents.CountAsync(e => e.WorkspaceId == alpha.Workspace.Id || e.WorkspaceId == beta.Workspace.Id)).Should().Be(0);

        (await db.WorkspaceMembers.IgnoreQueryFilters().CountAsync(m => m.WorkspaceId == alpha.Workspace.Id)).Should().Be(1);
        (await db.Invitations.IgnoreQueryFilters().CountAsync(i => i.Email == pending)).Should().Be(1);
        (await db.WorkspaceMembershipEvents.IgnoreQueryFilters().CountAsync(e => e.WorkspaceId == beta.Workspace.Id)).Should().Be(1);

        tenant.SetWorkspace(alpha.Workspace.Id);
        var members = await db.WorkspaceMembers.Select(m => m.WorkspaceId).ToListAsync();
        members.Should().OnlyContain(id => id == alpha.Workspace.Id);
        members.Should().NotBeEmpty();
        (await db.Invitations.Select(i => i.WorkspaceId).ToListAsync()).Should().OnlyContain(id => id == alpha.Workspace.Id);
        (await db.Projects.AnyAsync(p => p.WorkspaceId == beta.Workspace.Id)).Should().BeFalse();
        (await db.WorkspaceMembershipEvents.AnyAsync(e => e.WorkspaceId == beta.Workspace.Id)).Should().BeFalse();

        var leaked = await beta.Client.GetAsync($"/api/workspaces/{alpha.Workspace.Id}/members");
        var leakedBody = await leaked.Content.ReadAsStringAsync();
        leaked.StatusCode.Should().Be(HttpStatusCode.NotFound, leakedBody);
        leakedBody.Should().NotContain(alpha.Tokens.User.Email);
    }

    [Fact]
    public async Task Invite_DuplicateMember_ExpiredInvalidWrongEmail_AndReuse()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var existing = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, existing.Tokens.User.Email, WorkspaceRole.Member);

        var duplicate = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(existing.Tokens.User.Email, WorkspaceRole.Member));
        await AssertProblem(duplicate, HttpStatusCode.Conflict, "workspace_member_exists",
            "This user is already a member of the workspace.");

        var wrongEmail = $"holder-{Guid.NewGuid():N}@example.com";
        var wrongToken = await InvitePendingAsync(owner, wrongEmail, WorkspaceRole.Viewer);
        var stranger = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var wrong = await stranger.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(wrongToken));
        await AssertProblem(wrong, HttpStatusCode.Forbidden, "forbidden",
            "This invitation was issued to a different email address.");
        await AssertRole(owner, stranger.Tokens.User.Id, null);

        var invalid = await stranger.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest("not-a-token"));
        await AssertProblem(invalid, HttpStatusCode.NotFound, "not_found", "Invitation is invalid or has expired.");

        var expiredEmail = $"expired-{Guid.NewGuid():N}@example.com";
        var expiredToken = await InvitePendingAsync(owner, expiredEmail, WorkspaceRole.Member);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Token == expiredToken);
            invitation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var expiredUser = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), expiredEmail);
        var expired = await expiredUser.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(expiredToken));
        await AssertProblem(expired, HttpStatusCode.NotFound, "not_found", "Invitation is invalid or has expired.");
        await AssertRole(owner, expiredUser.Tokens.User.Id, null);

        var reuseEmail = $"reuse-{Guid.NewGuid():N}@example.com";
        var reuseToken = await InvitePendingAsync(owner, reuseEmail, WorkspaceRole.Member);
        var reuseUser = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), reuseEmail);
        var first = await reuseUser.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(reuseToken));
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var second = await reuseUser.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(reuseToken));
        await AssertProblem(second, HttpStatusCode.NotFound, "not_found", "Invitation is invalid or has expired.");
        await AssertRole(owner, reuseUser.Tokens.User.Id, WorkspaceRole.Member);
    }

    [Fact]
    public async Task Accept_WhenTheInviterWasRemoved_IsForbidden()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, second.Tokens.User.Email, WorkspaceRole.Member);
        (await PatchRole(owner, second.Tokens.User.Id, WorkspaceRole.Owner)).EnsureSuccessStatusCode();

        var email = $"orphan-{Guid.NewGuid():N}@example.com";
        var token = await InvitePendingAsync(owner, email, WorkspaceRole.Member);
        (await second.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}"))
            .EnsureSuccessStatusCode();

        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);
        var accept = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        await AssertProblem(accept, HttpStatusCode.Forbidden, "forbidden",
            "The person who sent this invitation can no longer add members.");
        await AssertRole(second, invitee.Tokens.User.Id, null);
    }

    [Fact]
    public async Task RoleChangeAndRemoval_IncludeTheLastOwner_AndAMissingMember()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var demote = await PatchRole(owner, owner.Tokens.User.Id, WorkspaceRole.Admin);
        await AssertProblem(demote, HttpStatusCode.Conflict, "last_owner",
            "Cannot remove or demote the last Owner of a workspace.");
        var remove = await owner.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}");
        await AssertProblem(remove, HttpStatusCode.Conflict, "last_owner",
            "Cannot remove or demote the last Owner of a workspace.");
        await AssertRole(owner, owner.Tokens.User.Id, WorkspaceRole.Owner);

        var same = await PatchRole(owner, owner.Tokens.User.Id, WorkspaceRole.Owner);
        same.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var missingId = Guid.NewGuid();
        var missingPatch = await PatchRole(owner, missingId, WorkspaceRole.Member);
        await AssertProblem(missingPatch, HttpStatusCode.NotFound, "not_found", "WorkspaceMember");
        var missingDelete = await owner.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{missingId}");
        await AssertProblem(missingDelete, HttpStatusCode.NotFound, "not_found", "WorkspaceMember");

        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, member.Tokens.User.Email, WorkspaceRole.Member);
        (await PatchRole(owner, member.Tokens.User.Id, WorkspaceRole.Admin)).EnsureSuccessStatusCode();
        (await owner.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{member.Tokens.User.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, member.Tokens.User.Id, null);

        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, second.Tokens.User.Email, WorkspaceRole.Member);
        (await PatchRole(owner, second.Tokens.User.Id, WorkspaceRole.Owner)).EnsureSuccessStatusCode();
        (await second.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var last = await second.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{second.Tokens.User.Id}");
        await AssertProblem(last, HttpStatusCode.Conflict, "last_owner",
            "Cannot remove or demote the last Owner of a workspace.");
        await AssertRole(second, second.Tokens.User.Id, WorkspaceRole.Owner);
    }

    [Fact]
    public async Task Validation_Returns400_ForWorkspaceInviteAcceptAndRole()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var blank = await owner.Client.PostAsJsonAsync("/api/workspaces", new { name = "   " });
        await AssertField(blank, "Name", "not_empty");

        var tooLong = await owner.Client.PostAsJsonAsync("/api/workspaces", new { name = new string('a', 201) });
        await AssertField(tooLong, "Name", "maximum_length");

        var badEmail = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new { email = "not-an-email", role = "Member" });
        await AssertField(badEmail, "Email", "email");

        var ownerRole = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest($"owner-{Guid.NewGuid():N}@example.com", WorkspaceRole.Owner));
        var ownerBody = await ownerRole.Content.ReadAsStringAsync();
        ownerRole.StatusCode.Should().Be(HttpStatusCode.BadRequest, ownerBody);
        JsonDocument.Parse(ownerBody).RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        ownerBody.Should().Contain("Cannot invite a member directly as Owner.");

        var emptyToken = await owner.Client.PostAsJsonAsync("/api/invitations/accept", new { token = "" });
        await AssertField(emptyToken, "Token", "not_empty");

        var badRole = await owner.Client.PatchAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}",
            new { role = "Nope" });
        var badRoleBody = await badRole.Content.ReadAsStringAsync();
        badRole.StatusCode.Should().Be(HttpStatusCode.BadRequest, badRoleBody);
        JsonDocument.Parse(badRoleBody).RootElement.GetProperty("code").GetString().Should().Be("invalid_request_body");
    }

    private static async Task<HttpResponseMessage> SendAs(
        RegisteredUser owner,
        string name,
        Guid workspaceId,
        Guid target,
        InviteMemberRequest invite)
    {
        return name switch
        {
            "GET members" => await owner.Client.GetAsync($"/api/workspaces/{workspaceId}/members"),
            "POST invitations" => await owner.Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", invite),
            "PATCH role" => await owner.Client.PatchAsJsonAsync(
                $"/api/workspaces/{workspaceId}/members/{target}",
                new UpdateMemberRoleRequest(WorkspaceRole.Member)),
            "DELETE member" => await owner.Client.DeleteAsync($"/api/workspaces/{workspaceId}/members/{target}"),
            _ => throw new InvalidOperationException(name)
        };
    }

    private static async Task InviteAsync(RegisteredUser owner, string email, WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, role));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> InvitePendingAsync(RegisteredUser owner, string email, WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, role));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var invitation = JsonSerializer.Deserialize<InvitationDto>(body, TestAuthHelper.JsonOptions);
        var link = invitation!.InviteLink ?? throw new InvalidOperationException("Pending invite did not return a link.");
        const string marker = "token=";
        var start = link.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        return Uri.UnescapeDataString(link[(start + marker.Length)..]);
    }

    private static Task<HttpResponseMessage> PatchRole(RegisteredUser actor, Guid userId, WorkspaceRole role) =>
        actor.Client.PatchAsJsonAsync(
            $"/api/workspaces/{actor.Workspace.Id}/members/{userId}",
            new UpdateMemberRoleRequest(role));

    private static async Task AssertRole(RegisteredUser reader, Guid userId, WorkspaceRole? expected)
    {
        var response = await reader.Client.GetAsync($"/api/workspaces/{reader.Workspace.Id}/members");
        response.EnsureSuccessStatusCode();
        var members = await response.Content.ReadFromJsonAsync<List<WorkspaceMemberDto>>(TestAuthHelper.JsonOptions);
        members!.SingleOrDefault(m => m.UserId == userId)?.Role.Should().Be(expected);
    }

    private static async Task AssertAccessDenied(HttpResponseMessage response)
    {
        await AssertProblem(response, HttpStatusCode.Forbidden, "workspace_access_denied",
            "You do not have sufficient permissions in this workspace.");
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code, string detailFragment)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("detail").GetString().Should().Contain(detailFragment);
    }

    private static async Task AssertField(HttpResponseMessage response, string field, string fieldCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("fieldCodes").GetProperty(field).EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain(fieldCode);
    }

    private static string Normalize(string body, Guid id) =>
        body.Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
}
