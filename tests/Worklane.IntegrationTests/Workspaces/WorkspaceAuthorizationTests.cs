using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class WorkspaceAuthorizationTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public WorkspaceAuthorizationTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NonMember_CannotReadAnotherWorkspacesMembers()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var response = await outsider.Client.GetAsync($"/api/workspaces/{owner.Workspace.Id}/members");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NonMember_CannotCreateProjectInAnotherWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var response = await outsider.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            new CreateProjectRequest("Should Fail", "SF", null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MemberRole_CanCreateProject_ButCannotChangeRolesOrInvite()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        await InviteAsync(owner, member, WorkspaceRole.Member);

        var createProjectResponse = await member.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            new CreateProjectRequest("Member Project", "MP", null));
        createProjectResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateRoleResponse = await member.Client.PatchAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/members/{member.Tokens.User.Id}",
            new UpdateMemberRoleRequest(WorkspaceRole.Admin));
        updateRoleResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var inviteAnotherResponse = await member.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest("someone-else@example.com", WorkspaceRole.Member));
        inviteAnotherResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CannotDemote_TheLastOwnerOfAWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var response = await owner.Client.PatchAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}",
            new UpdateMemberRoleRequest(WorkspaceRole.Admin));

        await AssertProblem(response, HttpStatusCode.Conflict, "Conflict", "last_owner",
            "Cannot remove or demote the last Owner of a workspace.");
    }

    [Fact]
    public async Task ProjectRoute_ResolvesWorkspaceFromProjectId_ForCrossTenantCheck()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var createResponse = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            new CreateProjectRequest("Owner Project", "OP", null));
        var project = await createResponse.Content.ReadFromJsonAsync<ProjectDto>();

        var archiveResponse = await outsider.Client.DeleteAsync($"/api/projects/{project!.Id}");

        archiveResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_CannotPromoteSelfToOwner_AndRoleStaysAdmin()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();

        var before = await EventCountAsync(owner.Workspace.Id);
        var response = await PatchRole(admin, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Owner);

        await AssertForbidden(response, "owner_role_owner_only", "Owner");
        await AssertRole(owner, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Admin);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task Admin_CannotGrantOwnerToSomeoneElse_AndRoleStays()
    {
        var (owner, admin, member) = await WorkspaceWithAdminAsync();

        var before = await EventCountAsync(owner.Workspace.Id);
        var response = await PatchRole(admin, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Owner);

        await AssertForbidden(response, "owner_role_owner_only", "Owner");
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Member);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task Admin_PromoteMemberToAdmin_FollowsAdminMayGrantAdmin()
    {
        var (owner, admin, member) = await WorkspaceWithAdminAsync();
        var before = await EventCountAsync(owner.Workspace.Id);

        var response = await PatchRole(admin, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Admin);

        // AC #2 and #12 say 204. AdminMayGrantAdmin is false because an Admin manages only
        // Viewer and Member. Flip that constant to allow the promotion; this test follows it.
        await FollowSwitch(
            WorkspaceMembershipPolicy.AdminMayGrantAdmin,
            whenAllowed: async () =>
            {
                response.StatusCode.Should().Be(HttpStatusCode.NoContent);
                await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Admin);
                var audit = await SingleEventAsync(owner.Workspace.Id, member.Tokens.User.Id, WorkspaceMembershipAction.RoleChanged);
                audit.ActorUserId.Should().Be(admin.Tokens.User.Id);
                audit.OldRole.Should().Be(WorkspaceRole.Member);
                audit.NewRole.Should().Be(WorkspaceRole.Admin);
            },
            whenForbidden: async () =>
            {
                await AssertForbidden(response, "admin_grant_owner_only", "Admin");
                await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Member);
                (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
            });
    }

    [Fact]
    public async Task Admin_CannotDemoteOrRemoveOwner_EvenWhenThatOwnerIsTheLastOne()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();
        var before = await EventCountAsync(owner.Workspace.Id);

        var demote = await PatchRole(admin, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Viewer);
        await AssertForbidden(demote, "owner_role_owner_only", "Owner");
        demote.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        await AssertRole(owner, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Owner);

        var remove = await admin.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}");
        await AssertForbidden(remove, "owner_role_owner_only", "Owner");
        remove.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        await AssertRole(owner, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Owner);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task Admin_ChangeOrRemoveAnotherAdmin_FollowsPeerAdminSwitch()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();
        var peer = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, peer, WorkspaceRole.Admin);
        var before = await EventCountAsync(owner.Workspace.Id);

        var demote = await PatchRole(admin, owner.Workspace.Id, peer.Tokens.User.Id, WorkspaceRole.Member);
        var remove = await admin.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{peer.Tokens.User.Id}");

        // Flip WorkspaceMembershipPolicy.AdminMayManagePeerAdmins to allow both calls.
        // Nothing else in the policy changes with that constant.
        await FollowSwitch(
            WorkspaceMembershipPolicy.AdminMayManagePeerAdmins,
            whenAllowed: async () =>
            {
                demote.StatusCode.Should().Be(HttpStatusCode.NoContent);
                remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
                await AssertRole(owner, owner.Workspace.Id, peer.Tokens.User.Id, null);
            },
            whenForbidden: async () =>
            {
                await AssertForbidden(demote, "admin_manage_peer_owner_only", "another Admin");
                await AssertForbidden(remove, "admin_manage_peer_owner_only", "another Admin");
                await AssertRole(owner, owner.Workspace.Id, peer.Tokens.User.Id, WorkspaceRole.Admin);
                (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
            });
    }

    [Fact]
    public async Task Admin_CanMoveMemberBetweenViewerAndMember_AndRemoveAMember()
    {
        var (owner, admin, member) = await WorkspaceWithAdminAsync();
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, viewer, WorkspaceRole.Viewer);

        var toViewer = await PatchRole(admin, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Viewer);
        toViewer.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Viewer);

        var backToMember = await PatchRole(admin, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Member);
        backToMember.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Member);

        var viewerToMember = await PatchRole(admin, owner.Workspace.Id, viewer.Tokens.User.Id, WorkspaceRole.Member);
        viewerToMember.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var remove = await admin.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{member.Tokens.User.Id}");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, null);

        var removed = await SingleEventAsync(owner.Workspace.Id, member.Tokens.User.Id, WorkspaceMembershipAction.MemberRemoved);
        removed.ActorUserId.Should().Be(admin.Tokens.User.Id);
        removed.WorkspaceId.Should().Be(owner.Workspace.Id);
        removed.OldRole.Should().Be(WorkspaceRole.Member);
        removed.NewRole.Should().BeNull();
        removed.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Admin_CannotRemoveSelf()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();
        var before = await EventCountAsync(owner.Workspace.Id);

        var response = await admin.Client.DeleteAsync(
            $"/api/workspaces/{owner.Workspace.Id}/members/{admin.Tokens.User.Id}");

        await AssertForbidden(response, "admin_remove_owner_only", "Only an Owner can remove an Admin.");
        await AssertRole(owner, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Admin);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task Admin_CanDemoteSelfToMemberOrViewer()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();
        var otherAdmin = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, otherAdmin, WorkspaceRole.Admin);

        var toMember = await PatchRole(admin, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Member);
        toMember.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Member);

        var toViewer = await PatchRole(otherAdmin, owner.Workspace.Id, otherAdmin.Tokens.User.Id, WorkspaceRole.Viewer);
        toViewer.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, otherAdmin.Tokens.User.Id, WorkspaceRole.Viewer);

        var self = await SingleEventAsync(owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceMembershipAction.RoleChanged);
        self.ActorUserId.Should().Be(admin.Tokens.User.Id);
        self.OldRole.Should().Be(WorkspaceRole.Admin);
        self.NewRole.Should().Be(WorkspaceRole.Member);
    }

    [Fact]
    public async Task Owner_CanGrantOwner_AndThatChangeIsAudited()
    {
        var (owner, _, member) = await WorkspaceWithAdminAsync();

        var response = await PatchRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Owner);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Owner);

        var audit = await SingleEventAsync(owner.Workspace.Id, member.Tokens.User.Id, WorkspaceMembershipAction.RoleChanged);
        audit.ActorUserId.Should().Be(owner.Tokens.User.Id);
        audit.WorkspaceId.Should().Be(owner.Workspace.Id);
        audit.OldRole.Should().Be(WorkspaceRole.Member);
        audit.NewRole.Should().Be(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task Owner_CanDemoteAndRemoveAdmin()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();
        var otherAdmin = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, otherAdmin, WorkspaceRole.Admin);

        var demote = await PatchRole(owner, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Viewer);
        demote.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceRole.Viewer);

        var remove = await owner.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{otherAdmin.Tokens.User.Id}");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, otherAdmin.Tokens.User.Id, null);

        var audit = await SingleEventAsync(owner.Workspace.Id, otherAdmin.Tokens.User.Id, WorkspaceMembershipAction.MemberRemoved);
        audit.ActorUserId.Should().Be(owner.Tokens.User.Id);
        audit.OldRole.Should().Be(WorkspaceRole.Admin);
        audit.NewRole.Should().BeNull();
    }

    [Fact]
    public async Task CannotRemove_TheLastOwnerOfAWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var response = await owner.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}");

        await AssertProblem(response, HttpStatusCode.Conflict, "Conflict", "last_owner",
            "Cannot remove or demote the last Owner of a workspace.");
        await AssertRole(owner, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Owner);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(0);
    }

    [Fact]
    public async Task OwnerOfAnotherWorkspace_CannotChangeOrRemoveMembersHere()
    {
        var (owner, _, member) = await WorkspaceWithAdminAsync();
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var before = await EventCountAsync(owner.Workspace.Id);

        var patch = await PatchRole(outsider, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Owner);
        var delete = await outsider.Client.DeleteAsync($"/api/workspaces/{owner.Workspace.Id}/members/{owner.Tokens.User.Id}");

        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Member);
        await AssertRole(owner, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Owner);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task OwnerElsewhere_WhoIsOnlyAnAdminHere_CannotGrantOwner()
    {
        var (owner, _, member) = await WorkspaceWithAdminAsync();
        var ownerElsewhere = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, ownerElsewhere, WorkspaceRole.Admin);
        var before = await EventCountAsync(owner.Workspace.Id);

        var response = await PatchRole(ownerElsewhere, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Owner);

        await AssertForbidden(response, "owner_role_owner_only", "Owner");
        await AssertRole(owner, owner.Workspace.Id, member.Tokens.User.Id, WorkspaceRole.Member);
        await AssertRole(ownerElsewhere, ownerElsewhere.Workspace.Id, ownerElsewhere.Tokens.User.Id, WorkspaceRole.Owner);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task MembershipAuditMigration_DownDropsTheTable_AndUpRecreatesIt()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();

        try
        {
            await db.Database.MigrateAsync("20261017195243_RedactDeletedCommentActivity");
            (await AuditTableCountAsync(db)).Should().Be(0);
        }
        finally
        {
            await db.Database.MigrateAsync();
        }

        (await AuditTableCountAsync(db)).Should().Be(1);
    }

    [Fact]
    public async Task Invite_AsOwner_Is400()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var target = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(target.Tokens.User.Email, WorkspaceRole.Owner));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Cannot invite a member directly as Owner.");
        await AssertRole(owner, owner.Workspace.Id, target.Tokens.User.Id, null);
        (await EventCountAsync(owner.Workspace.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Admin_InviteAdmin_Is403_AndDoesNotAddTheMember()
    {
        var (owner, admin, _) = await WorkspaceWithAdminAsync();
        var added = await SingleEventAsync(owner.Workspace.Id, admin.Tokens.User.Id, WorkspaceMembershipAction.MemberAdded);
        added.ActorUserId.Should().Be(owner.Tokens.User.Id);
        added.OldRole.Should().BeNull();
        added.NewRole.Should().Be(WorkspaceRole.Admin);

        var before = await EventCountAsync(owner.Workspace.Id);
        var existing = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var existingResponse = await admin.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(existing.Tokens.User.Email, WorkspaceRole.Admin));

        await AssertForbidden(existingResponse, "admin_grant_owner_only", "Admin");
        await AssertRole(owner, owner.Workspace.Id, existing.Tokens.User.Id, null);

        var pendingEmail = $"pending-{Guid.NewGuid():N}@example.com";
        var pendingResponse = await admin.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(pendingEmail, WorkspaceRole.Admin));
        await AssertForbidden(pendingResponse, "admin_grant_owner_only", "Admin");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var stored = await db.Invitations.IgnoreQueryFilters().AnyAsync(i => i.Email == pendingEmail);
            stored.Should().BeFalse();
        }

        (await EventCountAsync(owner.Workspace.Id)).Should().Be(before);
    }

    [Fact]
    public async Task AcceptInvitation_GrantsTheInvitedRole_AndRecordsTheMember()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var token = await InvitePendingAsync(owner, email, WorkspaceRole.Admin);

        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);
        var accept = await invitee.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(token));

        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(owner, owner.Workspace.Id, invitee.Tokens.User.Id, WorkspaceRole.Admin);

        var audit = await SingleEventAsync(owner.Workspace.Id, invitee.Tokens.User.Id, WorkspaceMembershipAction.MemberAdded);
        audit.ActorUserId.Should().Be(owner.Tokens.User.Id);
        audit.WorkspaceId.Should().Be(owner.Workspace.Id);
        audit.OldRole.Should().BeNull();
        audit.NewRole.Should().Be(WorkspaceRole.Admin);
    }

    [Fact]
    public async Task AcceptInvitation_RevalidatesAgainstTheInvitersCurrentRole()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await AddSecondOwnerAsync(owner);
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var token = await InvitePendingAsync(owner, email, WorkspaceRole.Admin);

        var demote = await PatchRole(second, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Admin);
        demote.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);
        var accept = await invitee.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(token));

        await AssertForbidden(accept, "admin_grant_owner_only", "Admin");
        await AssertRole(second, owner.Workspace.Id, invitee.Tokens.User.Id, null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.TokenHash == InviteTokens.Hash(token));
        invitation.AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task TwoOwners_DemotingEachOtherConcurrently_LeavesAtLeastOneOwner()
    {
        var first = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await AddSecondOwnerAsync(first);
        var workspaceId = first.Workspace.Id;

        for (var round = 0; round < 12; round++)
        {
            var demoteFirst = PatchRole(second, workspaceId, first.Tokens.User.Id, WorkspaceRole.Member);
            var demoteSecond = PatchRole(first, workspaceId, second.Tokens.User.Id, WorkspaceRole.Member);
            var results = await Task.WhenAll(demoteFirst, demoteSecond);

            foreach (var response in results)
            {
                if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.Forbidden or HttpStatusCode.Conflict))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    response.Dispose();
                    throw new InvalidOperationException($"Unexpected {(int)response.StatusCode} on round {round}: {body}");
                }

                response.Dispose();
            }

            (await OwnerCountAsync(workspaceId)).Should().BeGreaterThan(0);
            await RestoreOwnerAsync(first, second, workspaceId);
            await RestoreOwnerAsync(second, first, workspaceId);
        }
    }

    [Fact]
    public async Task Owner_CanDemoteSelf_WhenAnotherOwnerExists()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var second = await AddSecondOwnerAsync(owner);

        var response = await PatchRole(owner, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Member);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRole(second, owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceRole.Member);
        await AssertRole(second, owner.Workspace.Id, second.Tokens.User.Id, WorkspaceRole.Owner);

        var audit = await SingleEventAsync(owner.Workspace.Id, owner.Tokens.User.Id, WorkspaceMembershipAction.RoleChanged);
        audit.ActorUserId.Should().Be(owner.Tokens.User.Id);
        audit.OldRole.Should().Be(WorkspaceRole.Owner);
        audit.NewRole.Should().Be(WorkspaceRole.Member);
    }

    private async Task<(RegisteredUser Owner, RegisteredUser Admin, RegisteredUser Member)> WorkspaceWithAdminAsync()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var admin = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, admin, WorkspaceRole.Admin);
        await InviteAsync(owner, member, WorkspaceRole.Member);
        return (owner, admin, member);
    }

    private static async Task InviteAsync(RegisteredUser owner, RegisteredUser user, WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(user.Tokens.User.Email, role));
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var invitation = JsonSerializer.Deserialize<InvitationDto>(body, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Invite did not return a body.");
        var link = invitation.InviteLink ?? throw new InvalidOperationException("Invite did not return a link.");
        var accept = await user.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(TestAuthHelper.TokenFromInviteLink(link)));
        accept.EnsureSuccessStatusCode();
    }

    private async Task<RegisteredUser> AddSecondOwnerAsync(RegisteredUser owner)
    {
        var second = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await InviteAsync(owner, second, WorkspaceRole.Member);
        var promote = await PatchRole(owner, owner.Workspace.Id, second.Tokens.User.Id, WorkspaceRole.Owner);
        promote.EnsureSuccessStatusCode();
        return second;
    }

    private static async Task<string> InvitePendingAsync(RegisteredUser owner, string email, WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, role));
        response.EnsureSuccessStatusCode();
        var invitation = await response.Content.ReadFromJsonAsync<InvitationDto>(TestAuthHelper.JsonOptions);
        var link = invitation!.InviteLink ?? throw new InvalidOperationException("Pending invite did not return a link.");
        return TestAuthHelper.TokenFromInviteLink(link);
    }

    private async Task<int> OwnerCountAsync(Guid workspaceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.WorkspaceMembers
            .IgnoreQueryFilters()
            .CountAsync(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Owner);
    }

    private async Task RestoreOwnerAsync(RegisteredUser candidate, RegisteredUser other, Guid workspaceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var role = await db.WorkspaceMembers
            .IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == candidate.Tokens.User.Id)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync();

        if (role == WorkspaceRole.Owner)
            return;

        var response = await PatchRole(other, workspaceId, candidate.Tokens.User.Id, WorkspaceRole.Owner);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> PatchRole(RegisteredUser actor, Guid workspaceId, Guid userId, WorkspaceRole role)
    {
        return await actor.Client.PatchAsJsonAsync(
            $"/api/workspaces/{workspaceId}/members/{userId}",
            new UpdateMemberRoleRequest(role));
    }

    private static async Task AssertRole(RegisteredUser reader, Guid workspaceId, Guid userId, WorkspaceRole? expected)
    {
        var response = await reader.Client.GetAsync($"/api/workspaces/{workspaceId}/members");
        response.EnsureSuccessStatusCode();
        var members = await response.Content.ReadFromJsonAsync<List<WorkspaceMemberDto>>(TestAuthHelper.JsonOptions);
        WorkspaceRole? role = members!.SingleOrDefault(m => m.UserId == userId)?.Role;
        role.Should().Be(expected);
    }

    private static Task AssertForbidden(HttpResponseMessage response, string code, string detailFragment)
        => AssertProblem(response, HttpStatusCode.Forbidden, "Forbidden", code, detailFragment);

    private static async Task AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode status,
        string title,
        string code,
        string detailFragment)
    {
        response.StatusCode.Should().Be(status);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problem = document.RootElement;
        problem.GetProperty("status").GetInt32().Should().Be((int)status);
        problem.GetProperty("title").GetString().Should().Be(title);
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("detail").GetString().Should().Contain(detailFragment);
    }

    private async Task<List<WorkspaceMembershipEvent>> EventsAsync(Guid workspaceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        // The audit filter is fail-closed, and this scope has no tenant pinned.
        return await db.WorkspaceMembershipEvents
            .IgnoreQueryFilters()
            .Where(e => e.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    private async Task<int> EventCountAsync(Guid workspaceId) => (await EventsAsync(workspaceId)).Count;

    private async Task<WorkspaceMembershipEvent> SingleEventAsync(Guid workspaceId, Guid targetUserId, WorkspaceMembershipAction action)
    {
        var events = await EventsAsync(workspaceId);
        return events.Should().ContainSingle(e => e.TargetUserId == targetUserId && e.Action == action).Subject;
    }

    /// <summary>
    /// The switch is a const, so the branch lives in this method. Otherwise the compiler
    /// deletes the unused side and a flip looks like a test change instead of a policy change.
    /// </summary>
    private static async Task FollowSwitch(bool allowed, Func<Task> whenAllowed, Func<Task> whenForbidden)
    {
        if (allowed)
            await whenAllowed();
        else
            await whenForbidden();
    }

    private static async Task<int> AuditTableCountAsync(WorklaneDbContext db)
    {
        var count = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::int AS "Value"
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'WorkspaceMembershipEvents'
            """)
            .ToListAsync();
        return count.Single();
    }
}

/// <summary>
/// The peer-Admin rows follow <see cref="WorkspaceMembershipPolicy.AdminMayManagePeerAdmins"/>.
/// Member → Admin follows <see cref="WorkspaceMembershipPolicy.AdminMayGrantAdmin"/>.
/// Every other row stays put when either constant is flipped.
/// </summary>
public class WorkspaceMembershipPolicyTests
{
    private static readonly WorkspaceRole Owner = WorkspaceRole.Owner;
    private static readonly WorkspaceRole Admin = WorkspaceRole.Admin;
    private static readonly WorkspaceRole Member = WorkspaceRole.Member;
    private static readonly WorkspaceRole Viewer = WorkspaceRole.Viewer;

    [Fact]
    public void PeerAdminRows_FollowTheSingleSwitch_AndOwnerRulesDoNot()
    {
        // These two calls are the only ones that change when AdminMayManagePeerAdmins flips.
        Expect(WorkspaceMembershipPolicy.AdminMayManagePeerAdmins, Admin, Admin, Member, isSelf: false);
        Expect(WorkspaceMembershipPolicy.AdminMayManagePeerAdmins, Admin, Admin, newRole: null, isSelf: false);

        // Granting Admin is the other switch. It is false today (stricter than AC #2).
        Expect(WorkspaceMembershipPolicy.AdminMayGrantAdmin, Admin, Member, Admin, isSelf: false);
        Expect(WorkspaceMembershipPolicy.AdminMayGrantAdmin, Admin, Viewer, Admin, isSelf: false);
        ExpectInvite(WorkspaceMembershipPolicy.AdminMayGrantAdmin, Admin, Admin);
        ExpectInvite(allowed: true, Owner, Admin);
        ExpectInvite(allowed: false, Admin, Owner);

        Allow(Owner, Member, Owner, isSelf: false);
        Allow(Owner, Admin, Viewer, isSelf: false);
        Allow(Owner, Admin, newRole: null, isSelf: false);
        Allow(Owner, Owner, Member, isSelf: true);

        Allow(Admin, Member, Viewer, isSelf: false);
        Allow(Admin, Viewer, Member, isSelf: false);
        Allow(Admin, Member, newRole: null, isSelf: false);
        Allow(Admin, Viewer, newRole: null, isSelf: false);
        Allow(Admin, Admin, Member, isSelf: true);
        Allow(Admin, Admin, Viewer, isSelf: true);

        Deny(Admin, Member, Owner, isSelf: false);
        Deny(Admin, Owner, Viewer, isSelf: false);
        Deny(Admin, Owner, newRole: null, isSelf: false);
        Deny(Admin, Admin, Owner, isSelf: true);
        Deny(Admin, Admin, newRole: null, isSelf: true);
        Deny(Member, Viewer, Member, isSelf: false);
    }

    [Fact]
    public void EachForbiddenRule_ThrowsItsCode()
    {
        ExpectCode(Member, Viewer, Member, isSelf: false, "member_manage_forbidden");
        ExpectCode(Admin, Member, Owner, isSelf: false, "owner_role_owner_only");
        ExpectCode(Admin, Owner, newRole: null, isSelf: false, "owner_role_owner_only");
        ExpectCode(Admin, Admin, newRole: null, isSelf: true, "admin_remove_owner_only");
        // Owner is rejected before this branch. A self change to a higher non-Owner role is the one that uses it.
        ExpectCode(Admin, Member, Admin, isSelf: true, "cannot_raise_own_role");

        if (!WorkspaceMembershipPolicy.AdminMayManagePeerAdmins)
            ExpectCode(Admin, Admin, Member, isSelf: false, "admin_manage_peer_owner_only");

        // No named role falls through: Owner and Admin return above, and Viewer and Member are allowed.
        ExpectCode(Admin, (WorkspaceRole)99, Viewer, isSelf: false, "admin_manages_viewer_member_only");

        if (!WorkspaceMembershipPolicy.AdminMayGrantAdmin)
        {
            ExpectCode(Admin, Viewer, Admin, isSelf: false, "admin_grant_owner_only");
            var invite = () => WorkspaceMembershipPolicy.EnsureCanInvite(Admin, Admin);
            invite.Should().Throw<ForbiddenAccessException>().Which.Code.Should().Be("admin_grant_owner_only");
        }
    }

    private static void Allow(WorkspaceRole actor, WorkspaceRole target, WorkspaceRole? newRole, bool isSelf)
        => Expect(allowed: true, actor, target, newRole, isSelf);

    private static void Deny(WorkspaceRole actor, WorkspaceRole target, WorkspaceRole? newRole, bool isSelf)
        => Expect(allowed: false, actor, target, newRole, isSelf);

    private static void Expect(bool allowed, WorkspaceRole actor, WorkspaceRole target, WorkspaceRole? newRole, bool isSelf)
    {
        var act = () => WorkspaceMembershipPolicy.EnsureCanManage(actor, target, newRole, isSelf);
        if (allowed)
            act.Should().NotThrow();
        else
            act.Should().Throw<ForbiddenAccessException>();
    }

    private static void ExpectCode(WorkspaceRole actor, WorkspaceRole target, WorkspaceRole? newRole, bool isSelf, string code)
    {
        var act = () => WorkspaceMembershipPolicy.EnsureCanManage(actor, target, newRole, isSelf);
        act.Should().Throw<ForbiddenAccessException>().Which.Code.Should().Be(code);
    }

    private static void ExpectInvite(bool allowed, WorkspaceRole actor, WorkspaceRole role)
    {
        var act = () => WorkspaceMembershipPolicy.EnsureCanInvite(actor, role);
        if (allowed)
            act.Should().NotThrow();
        else
            act.Should().Throw<ForbiddenAccessException>();
    }
}
