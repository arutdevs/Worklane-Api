using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Board.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class ClearTaskFieldsTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ClearTaskFieldsTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExplicitNull_ClearsOptionalFields_RecomputesFlags_AndRecordsEachChange()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "clear-owner@test.com", "Ada");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Clear me",
            description = "notes",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id,
            dueDate = DateTimeOffset.UtcNow.AddDays(-14),
            estimateDays = 1,
            columnId = board.Todo.Id
        });
        created.EnsureSuccessStatusCode();
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/move", new { toColumnId = board.InProgress.Id, toIndex = 0 }))
            .EnsureSuccessStatusCode();
        await BackdateStartAsync(task.Id, DateTimeOffset.UtcNow.AddDays(-9));

        var before = (await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        before.IsOverdue.Should().BeTrue();
        before.IsOverEstimate.Should().BeTrue();

        var cleared = await Patch(board, task.Id, """
            {"description":null,"assigneeId":null,"dueDate":null,"estimateDays":null}
            """);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await cleared.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        dto.Description.Should().BeNull();
        dto.AssigneeId.Should().BeNull();
        dto.AssigneeName.Should().BeNull();
        dto.DueDate.Should().BeNull();
        dto.EstimateDays.Should().BeNull();
        dto.IsOverdue.Should().BeFalse();
        dto.DaysOverdue.Should().Be(0);
        dto.IsOverEstimate.Should().BeFalse();
        dto.DaysOverEstimate.Should().Be(0);
        dto.Title.Should().Be("Clear me");

        var activity = (await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions))!;
        var clearedRows = activity
            .Where(a => a.Type == TaskActivityType.Updated && a.Details!.Value.GetProperty("newValue").ValueKind == JsonValueKind.Null)
            .Select(a => a.Details!.Value.GetProperty("field").GetString())
            .ToList();
        clearedRows.Should().BeEquivalentTo("description", "assigneeId", "dueDate", "estimateDays");
    }

    [Fact]
    public async Task OmittedField_StaysUnchanged()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "omit-owner@test.com");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Keep the note",
            description = "still here",
            priority = "Medium",
            columnId = board.Todo.Id
        });
        created.EnsureSuccessStatusCode();
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        var renamed = await Patch(board, task.Id, """{"title":"Renamed"}""");
        renamed.EnsureSuccessStatusCode();
        var dto = (await renamed.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        dto.Title.Should().Be("Renamed");
        dto.Description.Should().Be("still here");
    }

    [Fact]
    public async Task NullOnRequiredFields_IsNotNullable()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "required-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Stay");

        await AssertNotNullable(board, task.Id, """{"title":null}""", "Title");
        await AssertNotNullable(board, task.Id, """{"priority":null}""", "Priority");
        await AssertNotNullable(board, task.Id, """{"columnId":null}""", "ColumnId");

        var reloaded = (await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        reloaded.Title.Should().Be("Stay");
        reloaded.Priority.Should().Be(TaskPriority.Medium);
        reloaded.ColumnId.Should().Be(board.Todo.Id);
    }

    [Fact]
    public async Task SignalR_TaskUpdated_CarriesTheClearedDto()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "clear-hub@test.com");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Broadcast",
            description = "gone soon",
            priority = "Medium",
            columnId = board.Todo.Id
        });
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        await using var connection = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var updated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("TaskUpdated", payload => updated.TrySetResult(payload.Clone()));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinProject", board.Project.Id);

        (await Patch(board, task.Id, """{"description":null}""")).EnsureSuccessStatusCode();
        var payload = await updated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        payload.GetProperty("id").GetGuid().Should().Be(task.Id);
        payload.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static async Task<HttpResponseMessage> Patch(BoardContext board, Guid taskId, string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        return await board.Client.PatchAsync($"/api/tasks/{taskId}", body);
    }

    private static async Task AssertNotNullable(BoardContext board, Guid taskId, string json, string field)
    {
        var response = await Patch(board, taskId, json);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var problem = JsonDocument.Parse(text).RootElement;
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("fieldCodes").GetProperty(field)[0].GetString().Should().Be("not_nullable");
        problem.GetProperty("errors").GetProperty(field)[0].GetString().Should().Be("This field can't be cleared.");
    }

    private async Task BackdateStartAsync(Guid taskId, DateTimeOffset startedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var task = await db.TaskItems.IgnoreQueryFilters().SingleAsync(t => t.Id == taskId);
        task.StartedAt = startedAt;
        await db.SaveChangesAsync();
    }
}
