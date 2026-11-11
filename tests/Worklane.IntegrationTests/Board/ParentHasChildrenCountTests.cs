using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Worklane.Application.Board;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Common.Interfaces;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;
using Worklane.WebApi.Middleware;

namespace Worklane.IntegrationTests.Board;

public class ParentHasChildrenCountTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ParentHasChildrenCountTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BlockedDelete_ChildCountIsDirectChildrenInTheCallerWorkspace()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-count@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var active = await PostChildAsync(board, "Active", parent.Id);
        var archived = await PostChildAsync(board, "Archived", parent.Id);
        (await board.Client.DeleteAsync($"/api/tasks/{archived.Id}")).EnsureSuccessStatusCode();

        var grandchild = await BoardTestSetup.CreateTaskAsync(board, "Grandchild");
        await LinkInDatabaseAsync(grandchild.Id, active.Id, board.Project.Id);

        var sibling = await BoardTestSetup.CreateTaskAsync(board, "Sibling parent");
        await PostChildAsync(board, "Sibling child", sibling.Id);

        var archivedOnly = await BoardTestSetup.CreateTaskAsync(board, "Archived only");
        var onlyChild = await PostChildAsync(board, "Only archived", archivedOnly.Id);
        (await board.Client.DeleteAsync($"/api/tasks/{onlyChild.Id}")).EnsureSuccessStatusCode();

        var other = await BoardTestSetup.CreateAsync(_factory, "parent-count-other@test.com");
        var foreignParent = await BoardTestSetup.CreateTaskAsync(other, "Foreign parent");
        await PostChildAsync(other, "Foreign one", foreignParent.Id);
        await PostChildAsync(other, "Foreign two", foreignParent.Id);
        await PostChildAsync(other, "Foreign three", foreignParent.Id);

        await using var blockedScope = await RejectParentDeleteAsync(parent.Id);
        var detail = blockedScope.Error.ToString();
        var body = await ProblemForAsync(board.User.Workspace.Id, blockedScope.Error);
        body.GetProperty("code").GetString().Should().Be(ParentTaskErrors.ParentHasChildren);
        body.GetProperty("detail").GetString().Should().Be(ParentTaskErrors.ParentHasChildrenDetail);
        body.TryGetProperty(ParentTaskErrors.ChildCount, out var count).Should().BeTrue(body.GetRawText() + "\n" + detail);
        count.ValueKind.Should().Be(JsonValueKind.Number);
        count.GetInt32().Should().Be(2, detail);

        var foreign = await ProblemForAsync(other.User.Workspace.Id, blockedScope.Error);
        foreign.GetProperty(ParentTaskErrors.ChildCount).GetInt32().Should().Be(0, detail);

        await using var onlyArchived = await RejectParentDeleteAsync(archivedOnly.Id);
        var onlyBody = await ProblemForAsync(board.User.Workspace.Id, onlyArchived.Error);
        onlyBody.GetProperty("code").GetString().Should().Be(ParentTaskErrors.ParentHasChildren);
        onlyBody.GetProperty(ParentTaskErrors.ChildCount).GetInt32().Should().Be(1, onlyArchived.Error.ToString());
    }

    [Fact]
    public async Task DeleteWithNoChildren_DoesNotEmitParentHasChildren()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-count-zero@test.com");
        var solo = await BoardTestSetup.CreateTaskAsync(board, "Solo");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var row = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == solo.Id);
        db.TaskItems.Remove(row);
        await db.SaveChangesAsync();

        var gone = await db.TaskItems.IgnoreQueryFilters().AnyAsync(task => task.Id == solo.Id);
        gone.Should().BeFalse();
    }

    [Fact]
    public async Task CrossProjectParent_DoesNotSendChildCount()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-count-cross@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var otherProject = await CreateProjectAsync(board, "Other", "OT");
        var foreign = await CreateTaskInProjectAsync(board, otherProject, "Foreign");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var row = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == foreign.Id);
        row.ParentTaskId = parent.Id;
        var error = (await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>()).Which;

        scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetWorkspace(board.User.Workspace.Id);
        var mapped = await ParentLinkConflicts.TranslateAsync(error, scope.ServiceProvider, CancellationToken.None);
        mapped.Should().NotBeNull();
        mapped!.Code.Should().Be(ParentTaskErrors.ParentHasChildren);
        mapped.Params.Should().NotContainKey(ParentTaskErrors.ChildCount, error.ToString());

        var body = await ProblemForAsync(board.User.Workspace.Id, error);
        body.GetProperty("code").GetString().Should().Be(ParentTaskErrors.ParentHasChildren);
        body.TryGetProperty(ParentTaskErrors.ChildCount, out _).Should().BeFalse(error.ToString());
    }

    [Fact]
    public async Task OutsiderIsNotFound_ViewerIsForbidden()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-count-auth@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        await PostChildAsync(board, "Child", parent.Id);
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "parent-count-viewer@test.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "parent-count-out@test.com");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, viewer.Tokens.User.Email, "Viewer");

        var forbidden = await viewer.Client.DeleteAsync($"/api/tasks/{parent.Id}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BodyAsync(forbidden)).GetProperty("code").GetString().Should().Be("workspace_access_denied");

        var missing = Guid.NewGuid();
        var hidden = await outsider.Client.DeleteAsync($"/api/tasks/{parent.Id}");
        var absent = await board.Client.DeleteAsync($"/api/tasks/{missing}");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Normalize(await hidden.Content.ReadAsStringAsync(), parent.Id)
            .Should().Be(Normalize(await absent.Content.ReadAsStringAsync(), missing));
    }

    /// <summary>
    /// Keeps the context alive. <see cref="DbUpdateException.Entries"/> is how the
    /// mapper finds the parent, and disposing the context clears those entries.
    /// </summary>
    private async Task<DeleteRejection> RejectParentDeleteAsync(Guid parentId)
    {
        var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var row = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == parentId);
        db.TaskItems.Remove(row);
        var error = (await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>()).Which;
        return new DeleteRejection(scope, error);
    }

    private sealed class DeleteRejection : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;

        public DeleteRejection(AsyncServiceScope scope, DbUpdateException error)
        {
            _scope = scope;
            Error = error;
        }

        public DbUpdateException Error { get; }

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }

    private async Task LinkInDatabaseAsync(Guid taskId, Guid parentId, Guid projectId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "TaskItems" SET "ParentTaskId" = {parentId} WHERE "Id" = {taskId} AND "ProjectId" = {projectId}""");
        updated.Should().Be(1);
    }

    private async Task<JsonElement> ProblemForAsync(Guid workspaceId, Exception exception)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenantService>().SetWorkspace(workspaceId);

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Path = "/api/tasks/example";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        context.Response.ContentType.Should().StartWith("application/problem+json");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static async Task<Guid> CreateProjectAsync(BoardContext board, string name, string key)
    {
        var response = await board.Client.PostAsJsonAsync(
            $"/api/workspaces/{board.User.Workspace.Id}/projects",
            new { name, key });
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return project.GetProperty("id").GetGuid();
    }

    private static async Task<TaskItemDto> CreateTaskInProjectAsync(BoardContext board, Guid projectId, string title)
    {
        var columns = await board.Client.GetFromJsonAsync<List<BoardColumnDto>>(
            $"/api/projects/{projectId}/columns", TestAuthHelper.JsonOptions);
        var columnId = columns!.OrderBy(column => column.Order).First().Id;
        var response = await board.Client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title,
            priority = "Medium",
            columnId
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<TaskItemDto> PostChildAsync(BoardContext board, string title, Guid parentId)
    {
        var response = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title,
            priority = "Medium",
            parentTaskId = parentId
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions));

    private static string Normalize(string body, Guid id) =>
        body.Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
}
