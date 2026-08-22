using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

/// <summary>
/// Integration tests that verify task move/reorder behaviour, including
/// fractional-order calculation, column transfer, and in-column reordering.
/// Each test boots a fresh Postgres container via WorklaneWebApplicationFactory.
/// </summary>
public class TaskMoveReorderTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public TaskMoveReorderTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(HttpClient client, Guid workspaceId, Guid projectId, List<BoardColumnDto> columns)> SetupBoardAsync(string email)
    {
        var client = _factory.CreateClient();

        // Register & get token
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "Test1234!",
            displayName = "Tester"
        });
        reg.EnsureSuccessStatusCode();
        var auth = await reg.Content.ReadFromJsonAsync<AuthTokensResponse>(TestAuthHelper.JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth!.AccessToken);

        // Create workspace
        var ws = await (await client.PostAsJsonAsync("/api/workspaces", new { name = "Test WS" }))
            .Content.ReadFromJsonAsync<WorkspaceDto>(TestAuthHelper.JsonOptions);

        // Create project
        var project = await (await client.PostAsJsonAsync($"/api/workspaces/{ws!.Id}/projects",
            new { name = "Test Project", key = "TST" }))
            .Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions);

        // Load seeded columns
        var columns = await (await client.GetAsync($"/api/projects/{project!.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);

        return (client, ws.Id, project.Id, columns!);
    }

    private async Task<TaskItemDto> CreateTaskAsync(HttpClient client, Guid projectId, string title, Guid? columnId = null)
    {
        var res = await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title,
            priority = "Medium",
            columnId
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveTask_ToEmptyColumn_SetsOrderTo65536()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move1@test.com");
        var todoId = columns[0].Id;
        var inProgressId = columns[1].Id;

        var task = await CreateTaskAsync(client, projectId, "Task A", todoId);

        var res = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/move", new { toColumnId = inProgressId, toIndex = 0 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        moved!.ColumnId.Should().Be(inProgressId);
        moved.Order.Should().BeApproximately(65536.0, 0.001);
    }

    [Fact]
    public async Task MoveTask_ToIndex0_PlacesBefore_ExistingTask()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move2@test.com");
        var colId = columns[0].Id;

        var t1 = await CreateTaskAsync(client, projectId, "First", colId);
        var t2 = await CreateTaskAsync(client, projectId, "Second", colId);

        // Move t2 to index 0 (before t1)
        var res = await client.PostAsJsonAsync($"/api/tasks/{t2.Id}/move", new { toColumnId = colId, toIndex = 0 });
        var moved = await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        moved!.Order.Should().BeLessThan(t1.Order);
    }

    [Fact]
    public async Task MoveTask_ToIndexEnd_PlacesAfter_AllExisting()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move3@test.com");
        var colId = columns[0].Id;

        var t1 = await CreateTaskAsync(client, projectId, "A", colId);
        var t2 = await CreateTaskAsync(client, projectId, "B", colId);
        var t3 = await CreateTaskAsync(client, projectId, "Mover", columns[1].Id);

        // Move t3 to end of first column
        var res = await client.PostAsJsonAsync($"/api/tasks/{t3.Id}/move", new { toColumnId = colId, toIndex = 2 });
        var moved = await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        moved!.ColumnId.Should().Be(colId);
        moved.Order.Should().BeGreaterThan(t2.Order);
    }

    [Fact]
    public async Task MoveTask_BetweenTwo_AssignsMidpointOrder()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move4@test.com");
        var colId = columns[0].Id;

        var t1 = await CreateTaskAsync(client, projectId, "First", colId);
        var t2 = await CreateTaskAsync(client, projectId, "Second", colId);
        var t3 = await CreateTaskAsync(client, projectId, "Inserter", columns[1].Id);

        // Move t3 between t1 (index 0) and t2 (index 1) → toIndex = 1
        var res = await client.PostAsJsonAsync($"/api/tasks/{t3.Id}/move", new { toColumnId = colId, toIndex = 1 });
        var moved = await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        moved!.Order.Should().BeGreaterThan(t1.Order);
        moved.Order.Should().BeLessThan(t2.Order);
    }

    [Fact]
    public async Task CreateMultipleTasks_DisplayKeyIsSequential()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move5@test.com");
        var colId = columns[0].Id;

        var t1 = await CreateTaskAsync(client, projectId, "Alpha", colId);
        var t2 = await CreateTaskAsync(client, projectId, "Beta", colId);
        var t3 = await CreateTaskAsync(client, projectId, "Gamma", colId);

        t1.DisplayKey.Should().Be("TST-1");
        t2.DisplayKey.Should().Be("TST-2");
        t3.DisplayKey.Should().Be("TST-3");
    }

    [Fact]
    public async Task GetColumns_ReturnsTasksGroupedAndOrdered()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move6@test.com");
        var colId = columns[0].Id;

        await CreateTaskAsync(client, projectId, "Z Task", colId);
        await CreateTaskAsync(client, projectId, "A Task", colId);

        var res = await client.GetAsync($"/api/projects/{projectId}/columns");
        var board = await res.Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);

        var firstCol = board!.First(c => c.Id == colId);
        firstCol.Tasks.Should().HaveCount(2);
        firstCol.Tasks[0].Order.Should().BeLessThan(firstCol.Tasks[1].Order);
    }

    [Fact]
    public async Task ArchiveTask_RemovedFromBoard()
    {
        var (client, _, projectId, columns) = await SetupBoardAsync("move7@test.com");
        var colId = columns[0].Id;

        var task = await CreateTaskAsync(client, projectId, "To archive", colId);
        var del = await client.DeleteAsync($"/api/tasks/{task.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var board = await (await client.GetAsync($"/api/projects/{projectId}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        board!.First(c => c.Id == colId).Tasks.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Low")]
    [InlineData("Medium")]
    [InlineData("High")]
    [InlineData("Urgent")]
    public async Task CreateTask_PersistsEveryPriority(string priority)
    {
        var (client, _, projectId, columns) = await SetupBoardAsync($"priority-{priority}@test.com");

        var res = await client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new
        {
            title = $"{priority} task",
            priority,
            columnId = columns[0].Id
        });
        res.EnsureSuccessStatusCode();

        var created = await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        created!.Priority.ToString().Should().Be(priority);

        // Re-read from the database, not just the in-memory entity the write returned.
        var reloaded = await (await client.GetAsync($"/api/tasks/{created.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        reloaded!.Priority.ToString().Should().Be(priority);
    }
}
