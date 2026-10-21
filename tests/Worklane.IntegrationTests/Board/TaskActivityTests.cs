using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Worklane.Application.Board.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class TaskActivityTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public TaskActivityTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Activity_RecordsCreateMoveAndFieldChanges_NewestFirst()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "activity@test.com", "Ada");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Write the log");

        var renamed = await (await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { title = "Write the history" }))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        renamed!.Title.Should().Be("Write the history");

        var priority = await (await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new
        {
            priority = "High",
            estimateDays = 2
        })).Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        priority!.Priority.Should().Be(TaskPriority.High);

        var moved = await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/move",
            new { toColumnId = board.InProgress.Id, toIndex = 0 });
        moved.EnsureSuccessStatusCode();

        var response = await board.Client.GetAsync($"/api/tasks/{task.Id}/activity");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var activity = (await response.Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions))!;

        activity.Select(a => a.Type).Should().ContainInOrder(
            TaskActivityType.Moved,
            TaskActivityType.Updated,
            TaskActivityType.Updated,
            TaskActivityType.Updated,
            TaskActivityType.Created);
        activity.Should().OnlyContain(a => a.Actor.UserId == board.User.Tokens.User.Id && a.Actor.DisplayName == "Ada");
        activity.Should().BeInDescendingOrder(a => a.CreatedAt);

        var created = activity.Last();
        created.Details!.Value.GetProperty("columnId").GetGuid().Should().Be(board.Todo.Id);
        created.Details.Value.GetProperty("columnName").GetString().Should().Be("To Do");
        created.Details.Value.GetProperty("title").GetString().Should().Be("Write the log");

        var titleChange = activity.Single(a =>
            a.Type == TaskActivityType.Updated &&
            a.Details!.Value.GetProperty("field").GetString() == "title");
        titleChange.Details!.Value.GetProperty("oldValue").GetString().Should().Be("Write the log");
        titleChange.Details.Value.GetProperty("newValue").GetString().Should().Be("Write the history");

        var estimate = activity.Single(a =>
            a.Type == TaskActivityType.Updated &&
            a.Details!.Value.GetProperty("field").GetString() == "estimateDays");
        estimate.Details!.Value.GetProperty("oldValue").ValueKind.Should().Be(JsonValueKind.Null);
        estimate.Details.Value.GetProperty("newValue").GetDouble().Should().Be(2);

        var move = activity[0];
        move.Details!.Value.GetProperty("fromColumnId").GetGuid().Should().Be(board.Todo.Id);
        move.Details.Value.GetProperty("fromColumnName").GetString().Should().Be("To Do");
        move.Details.Value.GetProperty("toColumnId").GetGuid().Should().Be(board.InProgress.Id);
        move.Details.Value.GetProperty("toColumnName").GetString().Should().Be("In Progress");
    }

    [Fact]
    public async Task UnchangedPatch_DoesNotAppendAnUpdate()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "activity-noop@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Same");

        var response = await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { title = "Same" });
        response.EnsureSuccessStatusCode();

        var activity = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions);

        activity.Should().ContainSingle().Which.Type.Should().Be(TaskActivityType.Created);
    }

    [Fact]
    public async Task Outsider_CannotReadActivity()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "activity-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Private");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "activity-outsider@test.com");

        var response = await outsider.Client.GetAsync($"/api/tasks/{task.Id}/activity");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
