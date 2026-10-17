using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Worklane.Application.Board.Dtos;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class WipLimitTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public WipLimitTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task WipLimit_IsASoftCap_OnCreateAndMove()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wip@test.com");

        var createdColumn = await (await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/columns",
                new { name = "Review", wipLimit = 1 }))
            .Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions);
        createdColumn!.WipLimit.Should().Be(1);
        createdColumn.TaskCount.Should().Be(0);
        createdColumn.IsOverWipLimit.Should().BeFalse();

        var first = await BoardTestSetup.CreateTaskAsync(board, "One", createdColumn.Id);
        first.IsOverWipLimit.Should().BeFalse();

        var secondResponse = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Two",
            priority = "Medium",
            columnId = createdColumn.Id
        });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = (await secondResponse.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        second.IsOverWipLimit.Should().BeTrue();

        var columns = await (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var review = columns!.Single(c => c.Id == createdColumn.Id);
        review.TaskCount.Should().Be(2);
        review.WipLimit.Should().Be(1);
        review.IsOverWipLimit.Should().BeTrue();
        review.Tasks.Should().OnlyContain(t => t.IsOverWipLimit);

        var elsewhere = await BoardTestSetup.CreateTaskAsync(board, "Elsewhere");
        elsewhere.IsOverWipLimit.Should().BeFalse();
        var movedResponse = await board.Client.PostAsJsonAsync($"/api/tasks/{elsewhere.Id}/move",
            new { toColumnId = createdColumn.Id, toIndex = 0 });
        movedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = (await movedResponse.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        moved.ColumnId.Should().Be(createdColumn.Id);
        moved.IsOverWipLimit.Should().BeTrue();

        var cleared = await (await board.Client.PatchAsJsonAsync($"/api/columns/{createdColumn.Id}", new { wipLimit = (int?)null }))
            .Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions);
        cleared!.WipLimit.Should().BeNull();
        cleared.TaskCount.Should().Be(3);
        cleared.IsOverWipLimit.Should().BeFalse();
    }

    [Fact]
    public async Task PatchingOtherColumnFields_DoesNotClearTheLimit()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wip-omit@test.com");
        var column = await (await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/columns",
                new { name = "QA", wipLimit = 4 }))
            .Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions);

        var renamed = await (await board.Client.PatchAsJsonAsync($"/api/columns/{column!.Id}", new { name = "QA lane" }))
            .Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions);

        renamed!.Name.Should().Be("QA lane");
        renamed.WipLimit.Should().Be(4);
        renamed.IsOverWipLimit.Should().BeFalse();
    }

    [Fact]
    public async Task AtTheLimit_IsNotOver_AndANonPositiveLimit_IsRejected()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wip-edge@test.com");
        var column = await (await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/columns",
                new { name = "Exactly", wipLimit = 1 }))
            .Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions);

        await BoardTestSetup.CreateTaskAsync(board, "Fits", column!.Id);
        var columns = await (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var exactly = columns!.Single(c => c.Id == column.Id);
        exactly.TaskCount.Should().Be(1);
        exactly.IsOverWipLimit.Should().BeFalse();

        (await board.Client.PatchAsJsonAsync($"/api/columns/{column.Id}", new { wipLimit = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/columns", new { name = "Bad", wipLimit = -1 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var still = await (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        still!.Single(c => c.Id == column.Id).WipLimit.Should().Be(1);
    }

    [Fact]
    public async Task ColumnWithoutALimit_IsNeverOver()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wip-none@test.com");
        board.Todo.WipLimit.Should().BeNull();
        board.Todo.IsOverWipLimit.Should().BeFalse();

        var task = await BoardTestSetup.CreateTaskAsync(board, "Unlimited");
        task.IsOverWipLimit.Should().BeFalse();
    }
}
