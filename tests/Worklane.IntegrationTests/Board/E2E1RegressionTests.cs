using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Worklane.Application.Board.Dtos;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

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
    public async Task Task_CreateWithAssigneeAndDueDate_RejectsEmptyTitle_CommentAndMovePersist()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"e2e-card-{Guid.NewGuid():N}@example.com", "Niran");
        var due = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

        var empty = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id,
            dueDate = due,
            columnId = board.Todo.Id
        });
        await AssertValidation(empty, "Title", "not_empty");

        var before = await board.Client.GetFromJsonAsync<List<TaskItemDto>>(
            $"/api/projects/{board.Project.Id}/tasks",
            TestAuthHelper.JsonOptions);
        before.Should().BeEmpty();

        var createdResponse = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Ship signup",
            description = "QA regression card",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id,
            dueDate = due,
            columnId = board.Todo.Id
        });
        var createdText = await createdResponse.Content.ReadAsStringAsync();
        createdResponse.StatusCode.Should().Be(HttpStatusCode.Created, createdText);
        var created = JsonSerializer.Deserialize<TaskItemDto>(createdText, TestAuthHelper.JsonOptions);
        created!.Title.Should().Be("Ship signup");
        created.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        created.AssigneeName.Should().Be("Niran");
        created.DueDate.Should().Be(due);
        created.ColumnId.Should().Be(board.Todo.Id);

        var read = await board.Client.GetFromJsonAsync<TaskItemDto>($"/api/tasks/{created.Id}", TestAuthHelper.JsonOptions);
        read!.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        read.AssigneeName.Should().Be("Niran");
        read.DueDate.Should().Be(due);
        read.ColumnId.Should().Be(board.Todo.Id);

        var commentResponse = await board.Client.PostAsJsonAsync($"/api/tasks/{created.Id}/comments", new { body = "Signed off" });
        var commentText = await commentResponse.Content.ReadAsStringAsync();
        commentResponse.StatusCode.Should().Be(HttpStatusCode.Created, commentText);
        var comment = JsonSerializer.Deserialize<CommentDto>(commentText, TestAuthHelper.JsonOptions);
        comment!.Body.Should().Be("Signed off");
        comment.TaskId.Should().Be(created.Id);

        var comments = await board.Client.GetFromJsonAsync<List<CommentDto>>(
            $"/api/tasks/{created.Id}/comments",
            TestAuthHelper.JsonOptions);
        comments.Should().ContainSingle().Which.Body.Should().Be("Signed off");

        var moveResponse = await board.Client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new
        {
            toColumnId = board.InProgress.Id,
            toIndex = 0
        });
        var moveText = await moveResponse.Content.ReadAsStringAsync();
        moveResponse.StatusCode.Should().Be(HttpStatusCode.OK, moveText);

        var moved = await board.Client.GetFromJsonAsync<TaskItemDto>($"/api/tasks/{created.Id}", TestAuthHelper.JsonOptions);
        moved!.ColumnId.Should().Be(board.InProgress.Id);
        moved.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        moved.DueDate.Should().Be(due);

        var columns = await GetColumnsAsync(board.Client, board.Project.Id);
        columns.Single(column => column.Id == board.Todo.Id).Tasks.Should().NotContain(task => task.Id == created.Id);
        var inProgress = columns.Single(column => column.Id == board.InProgress.Id);
        var card = inProgress.Tasks.Should().ContainSingle(task => task.Id == created.Id).Subject;
        card.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        card.DueDate.Should().Be(due);

        var commentsAfterMove = await board.Client.GetFromJsonAsync<List<CommentDto>>(
            $"/api/tasks/{created.Id}/comments",
            TestAuthHelper.JsonOptions);
        commentsAfterMove.Should().ContainSingle().Which.Body.Should().Be("Signed off");
    }

    private static async Task<List<BoardColumnDto>> GetColumnsAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/projects/{projectId}/columns");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Columns were not returned.");
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
