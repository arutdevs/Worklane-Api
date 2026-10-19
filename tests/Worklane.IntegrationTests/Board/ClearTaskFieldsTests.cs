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
        var board = await BoardTestSetup.CreateAsync(_factory, "omit-owner@test.com", "Ada");
        var due = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Keep the note",
            description = "still here",
            priority = "High",
            assigneeId = board.User.Tokens.User.Id,
            dueDate = due,
            estimateDays = 3,
            columnId = board.Todo.Id
        });
        created.EnsureSuccessStatusCode();
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        var untouched = await Patch(board, task.Id, "{}");
        untouched.EnsureSuccessStatusCode();
        var same = (await untouched.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        AssertOmittedFields(same, task);

        var renamed = await Patch(board, task.Id, """{"title":"Renamed"}""");
        renamed.EnsureSuccessStatusCode();
        var dto = (await renamed.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        dto.Title.Should().Be("Renamed");
        dto.Description.Should().Be(task.Description);
        dto.Priority.Should().Be(task.Priority);
        dto.ColumnId.Should().Be(task.ColumnId);
        dto.AssigneeId.Should().Be(task.AssigneeId);
        dto.DueDate.Should().Be(task.DueDate);
        dto.EstimateDays.Should().Be(task.EstimateDays);
    }

    [Fact]
    public async Task SpecifiedValues_UpdateDueDateEstimateDescriptionAndMemberAssignee()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "set-owner@test.com");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "set-member@test.com", "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Fill me in");
        var due = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);

        var json = JsonSerializer.Serialize(new
        {
            description = "updated note",
            assigneeId = member.Tokens.User.Id,
            dueDate = due,
            estimateDays = 2.5
        }, TestAuthHelper.JsonOptions);
        var updated = await Patch(board, task.Id, json);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await updated.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        dto.Description.Should().Be("updated note");
        dto.AssigneeId.Should().Be(member.Tokens.User.Id);
        dto.AssigneeName.Should().Be("Member");
        dto.DueDate.Should().Be(due);
        dto.EstimateDays.Should().Be(2.5);
        dto.Title.Should().Be("Fill me in");
    }

    [Fact]
    public async Task DescriptionOverMaxLength_IsBadRequest()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "long-note-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Stay short");
        var json = JsonSerializer.Serialize(new { description = new string('a', 10_001) }, TestAuthHelper.JsonOptions);

        var response = await Patch(board, task.Id, json);
        await AssertValidation(response, "Description", "maximum_length");

        var reloaded = await GetTask(board, task.Id);
        reloaded.Description.Should().BeNull();
    }

    [Fact]
    public async Task EstimateDaysZero_IsBadRequest()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "zero-estimate-owner@test.com");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Keep the estimate",
            priority = "Medium",
            estimateDays = 3,
            columnId = board.Todo.Id
        });
        created.EnsureSuccessStatusCode();
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        var response = await Patch(board, task.Id, """{"estimateDays":0}""");
        await AssertValidation(response, "EstimateDays", "greater_than");

        var reloaded = await GetTask(board, task.Id);
        reloaded.EstimateDays.Should().Be(3);
    }

    [Fact]
    public async Task ClearingAssignee_RemovesTaskFromMyTasks()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "my-tasks-owner@test.com", "Ada");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Mine",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id,
            columnId = board.Todo.Id
        });
        created.EnsureSuccessStatusCode();
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        (await MyTaskIds(board)).Should().Contain(task.Id);

        (await Patch(board, task.Id, """{"assigneeId":null}""")).EnsureSuccessStatusCode();

        (await MyTaskIds(board)).Should().NotContain(task.Id);
    }

    [Fact]
    public async Task Swagger_RendersOptionalFieldsAsNullableValues()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var doc = JsonDocument.Parse(text).RootElement;
        var schemas = doc.GetProperty("components").GetProperty("schemas");

        var dueDate = schemas.GetProperty("UpdateTaskRequest").GetProperty("properties").GetProperty("dueDate");
        dueDate.TryGetProperty("$ref", out _).Should().BeFalse(dueDate.ToString());
        dueDate.TryGetProperty("properties", out _).Should().BeFalse(dueDate.ToString());
        dueDate.GetProperty("type").GetString().Should().Be("string");
        dueDate.GetProperty("format").GetString().Should().Be("date-time");
        dueDate.GetProperty("nullable").GetBoolean().Should().BeTrue();

        var wipLimit = schemas.GetProperty("UpdateColumnRequest").GetProperty("properties").GetProperty("wipLimit");
        wipLimit.TryGetProperty("properties", out _).Should().BeFalse(wipLimit.ToString());
        wipLimit.GetProperty("type").GetString().Should().Be("integer");
        wipLimit.GetProperty("nullable").GetBoolean().Should().BeTrue();

        schemas.EnumerateObject().Select(schema => schema.Name).Should().NotContain(name => name.Contains("Optional"));
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

    private static void AssertOmittedFields(TaskItemDto actual, TaskItemDto original)
    {
        actual.Title.Should().Be(original.Title);
        actual.Description.Should().Be(original.Description);
        actual.Priority.Should().Be(original.Priority);
        actual.ColumnId.Should().Be(original.ColumnId);
        actual.AssigneeId.Should().Be(original.AssigneeId);
        actual.DueDate.Should().Be(original.DueDate);
        actual.EstimateDays.Should().Be(original.EstimateDays);
    }

    private static async Task<TaskItemDto> GetTask(BoardContext board, Guid taskId)
    {
        var response = await board.Client.GetAsync($"/api/tasks/{taskId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<List<Guid>> MyTaskIds(BoardContext board)
    {
        var response = await board.Client.GetAsync($"/api/workspaces/{board.User.Workspace.Id}/my-tasks");
        response.EnsureSuccessStatusCode();
        var mine = (await response.Content.ReadFromJsonAsync<List<MyTaskDto>>(TestAuthHelper.JsonOptions))!;
        return mine.Select(item => item.Task.Id).ToList();
    }

    private static async Task AssertValidation(HttpResponseMessage response, string field, string fieldCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var problem = JsonDocument.Parse(text).RootElement;
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("fieldCodes").GetProperty(field).EnumerateArray().Select(item => item.GetString())
            .Should().Contain(fieldCode);
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
