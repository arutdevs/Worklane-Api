using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Worklane.Application.Board.Dtos;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class TaskCardCountTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public TaskCardCountTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TaskPayloads_IncludeCommentAndAttachmentCounts_WithoutOpeningTheCard()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "counts-owner@test.com", "Owner");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Counted",
            priority = "Medium",
            columnId = board.Todo.Id,
            assigneeId = board.User.Tokens.User.Id
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        task.CommentCount.Should().Be(0);
        task.AttachmentCount.Should().Be(0);

        var sibling = await BoardTestSetup.CreateTaskAsync(board, "Empty neighbour");
        sibling.CommentCount.Should().Be(0);
        sibling.AttachmentCount.Should().Be(0);

        var first = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "One" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Two" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await UploadAsync(board.Client, task.Id, [1, 2, 3], "image/png", "shot.png"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await AssertCounts(await GetTask(board, task.Id), 2, 1);
        await AssertCounts(await FindInProjectList(board, task.Id), 2, 1);
        await AssertCounts(await FindOnBoard(board, task.Id), 2, 1);
        await AssertCounts(await FindInMyTasks(board, task.Id), 2, 1);

        var updated = await (await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { title = "Counted, renamed" }))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        await AssertCounts(updated!, 2, 1);

        var moved = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/move",
                new { toColumnId = board.InProgress.Id, toIndex = 0 }))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        await AssertCounts(moved!, 2, 1);

        var neighbour = await FindOnBoard(board, sibling.Id);
        neighbour.CommentCount.Should().Be(0);
        neighbour.AttachmentCount.Should().Be(0);
        (await FindInProjectList(board, sibling.Id)).CommentCount.Should().Be(0);

        (await board.Client.DeleteAsync($"/api/comments/{first!.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertCounts(await GetTask(board, task.Id), 1, 1);

        var files = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/attachments"))
            .Content.ReadFromJsonAsync<List<AttachmentDto>>(TestAuthHelper.JsonOptions);
        (await board.Client.DeleteAsync($"/api/attachments/{files![0].Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertCounts(await GetTask(board, task.Id), 1, 0);
        await AssertCounts(await FindInMyTasks(board, task.Id), 1, 0);
    }

    private static async Task<TaskItemDto> GetTask(BoardContext board, Guid taskId)
    {
        var response = await board.Client.GetAsync($"/api/tasks/{taskId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<TaskItemDto> FindInProjectList(BoardContext board, Guid taskId)
    {
        var tasks = await (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/tasks"))
            .Content.ReadFromJsonAsync<List<TaskItemDto>>(TestAuthHelper.JsonOptions);
        return tasks!.Single(t => t.Id == taskId);
    }

    private static async Task<TaskItemDto> FindOnBoard(BoardContext board, Guid taskId)
    {
        var columns = await (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        return columns!.SelectMany(c => c.Tasks).Single(t => t.Id == taskId);
    }

    private static async Task<TaskItemDto> FindInMyTasks(BoardContext board, Guid taskId)
    {
        var mine = await (await board.Client.GetAsync($"/api/workspaces/{board.User.Workspace.Id}/my-tasks"))
            .Content.ReadFromJsonAsync<List<MyTaskDto>>(TestAuthHelper.JsonOptions);
        return mine!.Single(t => t.Task.Id == taskId).Task;
    }

    private static Task AssertCounts(TaskItemDto task, int comments, int attachments)
    {
        task.CommentCount.Should().Be(comments);
        task.AttachmentCount.Should().Be(attachments);
        return Task.CompletedTask;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid taskId, byte[] bytes, string contentType, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return await client.PostAsync($"/api/tasks/{taskId}/attachments", content);
    }
}
