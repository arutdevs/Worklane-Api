using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Projects.Interfaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.IntegrationTests.Web;

public class RequireWorkspaceRoleFilterTests
{
    [Fact]
    public async Task MissingUser_IsUnauthorized()
    {
        var problem = await Run(new RequireWorkspaceRoleAttribute(WorkspaceRole.Viewer), Guid.Empty, "ignored", null, null);
        problem.Status.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Extensions["code"].Should().Be("unauthorized");
    }

    [Fact]
    public async Task BadRouteId_IsInvalidRouteId()
    {
        var problem = await Run(new RequireWorkspaceRoleAttribute(WorkspaceRole.Viewer), Guid.NewGuid(), "not-a-guid", null, null);
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Extensions["code"].Should().Be("invalid_route_id");
    }

    [Fact]
    public async Task UnknownProject_IsNotFound()
    {
        var projectId = Guid.NewGuid();
        var problem = await Run(
            new RequireWorkspaceRoleAttribute(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId"),
            Guid.NewGuid(),
            projectId.ToString(),
            role: null,
            projectWorkspaceId: null,
            routeKey: "projectId");
        problem.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Extensions["code"].Should().Be("not_found");
    }

    [Fact]
    public async Task InsufficientRole_IsWorkspaceAccessDenied()
    {
        var workspaceId = Guid.NewGuid();
        var problem = await Run(
            new RequireWorkspaceRoleAttribute(WorkspaceRole.Admin),
            Guid.NewGuid(),
            workspaceId.ToString(),
            WorkspaceRole.Viewer,
            null);
        problem.Status.Should().Be(StatusCodes.Status403Forbidden);
        problem.Extensions["code"].Should().Be("workspace_access_denied");
    }

    private static async Task<ProblemDetails> Run(
        RequireWorkspaceRoleAttribute filter,
        Guid userId,
        string routeValue,
        WorkspaceRole? role,
        Guid? projectWorkspaceId,
        string routeKey = "id")
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ICurrentUserService>(new StubUser(userId))
                .AddSingleton<IWorkspaceService>(new StubWorkspaces(role))
                .AddSingleton<ICurrentTenantService>(new StubTenant())
                .AddSingleton<IProjectService>(new StubProjects(projectWorkspaceId))
                .BuildServiceProvider()
        };
        http.Request.Path = "/api/example";
        var route = new RouteData();
        route.Values[routeKey] = routeValue;
        var action = new ActionContext(http, route, new ActionDescriptor());
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());

        await filter.OnActionExecutionAsync(executing, () =>
            throw new InvalidOperationException("The filter should have rejected the request."));

        return executing.Result.Should().BeOfType<ObjectResult>().Which.Value.Should().BeOfType<ProblemDetails>().Subject;
    }

    private sealed class StubUser(Guid userId) : ICurrentUserService
    {
        public Guid UserId { get; } = userId;
        public string? Email => null;
    }

    private sealed class StubTenant : ICurrentTenantService
    {
        public Guid? WorkspaceId { get; private set; }
        public void SetWorkspace(Guid workspaceId) => WorkspaceId = workspaceId;
    }

    private sealed class StubProjects(Guid? workspaceId) : IProjectService
    {
        public Task<Guid?> GetWorkspaceIdForProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult(workspaceId);

        public Task<List<ProjectDto>> GetProjectsAsync(Guid workspaceId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<ProjectDto> CreateProjectAsync(Guid workspaceId, Guid createdByUserId, CreateProjectRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<ProjectDto> UpdateProjectAsync(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task ArchiveProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<bool> IsArchivedAsync(Guid projectId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class StubWorkspaces(WorkspaceRole? role) : IWorkspaceService
    {
        public Task<WorkspaceRole?> GetUserRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(role);

        public Task<List<WorkspaceDto>> GetMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<WorkspaceDto> CreateWorkspaceAsync(Guid ownerUserId, CreateWorkspaceRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<List<WorkspaceMemberDto>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<InvitationDto> InviteMemberAsync(Guid workspaceId, Guid invitedByUserId, InviteMemberRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task AcceptInvitationAsync(Guid acceptingUserId, string token, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task UpdateMemberRoleAsync(Guid workspaceId, Guid targetUserId, WorkspaceRole newRole, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task RemoveMemberAsync(Guid workspaceId, Guid targetUserId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
