using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
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

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonMember_CannotCreateProjectInAnotherWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var response = await outsider.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            new CreateProjectRequest("Should Fail", "SF", null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MemberRole_CanCreateProject_ButCannotChangeRolesOrInvite()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var inviteResponse = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(member.Tokens.User.Email, WorkspaceRole.Member));
        inviteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

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

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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

        archiveResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
