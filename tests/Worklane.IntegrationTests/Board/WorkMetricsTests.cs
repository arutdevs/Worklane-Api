using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Reports.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

/// <summary>
/// Covers the timing the board records as cards move, and the per-person report built
/// on top of it. Elapsed time is rewritten directly in the database where a test needs
/// a card to look older than the few milliseconds the test has been running.
/// </summary>
public class WorkMetricsTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public WorkMetricsTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Board(
        HttpClient Client,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid UserId,
        BoardColumnDto Todo,
        BoardColumnDto InProgress,
        BoardColumnDto Done);

    private async Task<Board> SetupAsync(string email)
    {
        var client = _factory.CreateClient();
        var user = await TestAuthHelper.RegisterAsync(client, email);

        var project = await (await client.PostAsJsonAsync($"/api/workspaces/{user.Workspace.Id}/projects",
            new { name = "Metrics Project", key = "MET" }))
            .Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions);

        var columns = await (await client.GetAsync($"/api/projects/{project!.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);

        return new Board(
            client,
            user.Workspace.Id,
            project.Id,
            user.Tokens.User.Id,
            columns!.Single(c => c.Name == "To Do"),
            columns!.Single(c => c.Name == "In Progress"),
            columns!.Single(c => c.Name == "Done"));
    }

    private async Task<TaskItemDto> CreateTaskAsync(
        Board board, string title, double? estimateDays = null, Guid? assigneeId = null, DateTimeOffset? dueDate = null)
    {
        var res = await board.Client.PostAsJsonAsync($"/api/projects/{board.ProjectId}/tasks", new
        {
            title,
            priority = "Medium",
            columnId = board.Todo.Id,
            estimateDays,
            assigneeId,
            dueDate
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private async Task<TaskItemDto> MoveAsync(Board board, Guid taskId, Guid toColumnId)
    {
        var res = await board.Client.PostAsJsonAsync($"/api/tasks/{taskId}/move",
            new { toColumnId, toIndex = 0 });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    /// <summary>Backdates a task's start so elapsed time is measurable inside a test run.</summary>
    private async Task BackdateStartAsync(Guid taskId, DateTimeOffset startedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var task = await db.TaskItems.IgnoreQueryFilters().SingleAsync(t => t.Id == taskId);
        task.StartedAt = startedAt;
        await db.SaveChangesAsync();
    }

    // ── Stamping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewTask_InTodoColumn_HasNoTiming()
    {
        var board = await SetupAsync("metrics1@test.com");
        var task = await CreateTaskAsync(board, "Untimed", estimateDays: 2);

        task.StartedAt.Should().BeNull();
        task.CompletedAt.Should().BeNull();
        task.ActualDays.Should().BeNull();
        task.EstimateDays.Should().Be(2);
    }

    [Fact]
    public async Task MovingIntoInProgress_StampsStartedAt()
    {
        var board = await SetupAsync("metrics2@test.com");
        var task = await CreateTaskAsync(board, "Starts the clock");

        var moved = await MoveAsync(board, task.Id, board.InProgress.Id);

        moved.StartedAt.Should().NotBeNull();
        moved.CompletedAt.Should().BeNull();
        moved.ActualDays.Should().NotBeNull();
    }

    [Fact]
    public async Task MovingIntoDone_StampsCompletedAt()
    {
        var board = await SetupAsync("metrics3@test.com");
        var task = await CreateTaskAsync(board, "Finishes");

        await MoveAsync(board, task.Id, board.InProgress.Id);
        var done = await MoveAsync(board, task.Id, board.Done.Id);

        done.StartedAt.Should().NotBeNull();
        done.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MovingBackOutOfDone_ClearsCompletedAt_ButKeepsStartedAt()
    {
        var board = await SetupAsync("metrics4@test.com");
        var task = await CreateTaskAsync(board, "Reopened");

        var started = await MoveAsync(board, task.Id, board.InProgress.Id);
        await MoveAsync(board, task.Id, board.Done.Id);
        var reopened = await MoveAsync(board, task.Id, board.InProgress.Id);

        reopened.CompletedAt.Should().BeNull();
        // Postgres stores microseconds, so a value read back is not bit-identical to the
        // one the write returned straight from the tracked entity.
        reopened.StartedAt.Should().BeCloseTo(started.StartedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ReenteringInProgress_DoesNotRestartTheClock()
    {
        var board = await SetupAsync("metrics5@test.com");
        var task = await CreateTaskAsync(board, "Bounced");

        var first = await MoveAsync(board, task.Id, board.InProgress.Id);
        await MoveAsync(board, task.Id, board.Todo.Id);
        var second = await MoveAsync(board, task.Id, board.InProgress.Id);

        second.StartedAt.Should().BeCloseTo(first.StartedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task EveryColumnChange_IsRecordedAsActivity()
    {
        var board = await SetupAsync("metrics6@test.com");
        var task = await CreateTaskAsync(board, "Tracked");

        await MoveAsync(board, task.Id, board.InProgress.Id);
        await MoveAsync(board, task.Id, board.Done.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var activity = await db.TaskActivities
            .IgnoreQueryFilters()
            .Where(a => a.TaskId == task.Id)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        // Creation, then the two moves.
        activity.Should().HaveCount(3);
        activity[0].FromColumnId.Should().BeNull();
        activity[1].ToColumnId.Should().Be(board.InProgress.Id);
        activity[2].ToColumnId.Should().Be(board.Done.Id);
    }

    [Fact]
    public async Task ReorderingWithinAColumn_RecordsNoActivity()
    {
        var board = await SetupAsync("metrics7@test.com");
        var first = await CreateTaskAsync(board, "First");
        await CreateTaskAsync(board, "Second");

        await MoveAsync(board, first.Id, board.Todo.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var moves = await db.TaskActivities
            .IgnoreQueryFilters()
            .CountAsync(a => a.TaskId == first.Id);

        // Only the creation entry — shuffling a card inside its column is not a transition.
        moves.Should().Be(1);
    }

    // ── Derived numbers ───────────────────────────────────────────────────────

    [Fact]
    public async Task TaskRunningPastItsEstimate_IsFlaggedWithTheOverrun()
    {
        var board = await SetupAsync("metrics8@test.com");
        var task = await CreateTaskAsync(board, "Slipping", estimateDays: 1);
        await MoveAsync(board, task.Id, board.InProgress.Id);

        // Started a full working week ago against a one-day estimate.
        await BackdateStartAsync(task.Id, DateTimeOffset.UtcNow.AddDays(-9));

        var reloaded = await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        reloaded!.IsOverEstimate.Should().BeTrue();
        reloaded.ActualDays.Should().BeGreaterThan(1);
        reloaded.DaysOverEstimate.Should().BeApproximately(reloaded.ActualDays!.Value - 1, 0.01);
    }

    [Fact]
    public async Task TaskPastItsDueDate_ReportsHowManyWorkingDaysLate()
    {
        var board = await SetupAsync("metrics9@test.com");
        var task = await CreateTaskAsync(board, "Late", dueDate: DateTimeOffset.UtcNow.AddDays(-14));

        var reloaded = await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        reloaded!.IsOverdue.Should().BeTrue();
        reloaded.DaysOverdue.Should().Be(10); // two calendar weeks = ten weekdays
    }

    [Fact]
    public async Task UnestimatedTask_IsNeverOverEstimate()
    {
        var board = await SetupAsync("metrics10@test.com");
        var task = await CreateTaskAsync(board, "No estimate");
        await MoveAsync(board, task.Id, board.InProgress.Id);
        await BackdateStartAsync(task.Id, DateTimeOffset.UtcNow.AddDays(-30));

        var reloaded = await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        reloaded!.IsOverEstimate.Should().BeFalse();
        reloaded.DaysOverEstimate.Should().Be(0);
    }

    // ── Report ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_SumsEstimatesAndSplitsWorkByAssignee()
    {
        var board = await SetupAsync("report1@test.com");
        var me = (await board.Client.GetFromJsonAsync<List<WorkspaceMemberDto>>(
            $"/api/workspaces/{board.WorkspaceId}/members", TestAuthHelper.JsonOptions))!.Single().UserId;

        await CreateTaskAsync(board, "Mine A", estimateDays: 2, assigneeId: me);
        await CreateTaskAsync(board, "Mine B", estimateDays: 3, assigneeId: me);
        await CreateTaskAsync(board, "Nobody's", estimateDays: 5);

        var report = await board.Client.GetFromJsonAsync<WorkspaceReportDto>(
            $"/api/workspaces/{board.WorkspaceId}/reports/members", TestAuthHelper.JsonOptions);

        report!.TotalCount.Should().Be(3);
        report.EstimateDays.Should().Be(10);
        report.UnestimatedCount.Should().Be(0);

        var mine = report.Members.Single(m => m.UserId == me);
        mine.TotalCount.Should().Be(2);
        mine.EstimateDays.Should().Be(5);

        var unassigned = report.Members.Single(m => m.UserId is null);
        unassigned.TotalCount.Should().Be(1);
        unassigned.EstimateDays.Should().Be(5);
    }

    [Fact]
    public async Task Report_CountsTasksByTheColumnTheySitIn()
    {
        var board = await SetupAsync("report2@test.com");
        var a = await CreateTaskAsync(board, "Waiting");
        var b = await CreateTaskAsync(board, "Working");
        var c = await CreateTaskAsync(board, "Finished");

        await MoveAsync(board, b.Id, board.InProgress.Id);
        await MoveAsync(board, c.Id, board.Done.Id);

        var report = await board.Client.GetFromJsonAsync<WorkspaceReportDto>(
            $"/api/workspaces/{board.WorkspaceId}/reports/members", TestAuthHelper.JsonOptions);

        report!.TodoCount.Should().Be(1);
        report.InProgressCount.Should().Be(1);
        report.DoneCount.Should().Be(1);
        _ = a;
    }

    [Fact]
    public async Task Report_ListsMembersWhoHaveNoTasks()
    {
        var board = await SetupAsync("report3@test.com");

        var report = await board.Client.GetFromJsonAsync<WorkspaceReportDto>(
            $"/api/workspaces/{board.WorkspaceId}/reports/members", TestAuthHelper.JsonOptions);

        // The owner appears even with an empty board, so the roster reads as a team.
        report!.Members.Should().ContainSingle();
        report.Members[0].TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Report_IsClosedToNonMembers()
    {
        var board = await SetupAsync("report4@test.com");

        var outsider = _factory.CreateClient();
        await TestAuthHelper.RegisterAsync(outsider, "outsider-report@test.com");

        var res = await outsider.GetAsync($"/api/workspaces/{board.WorkspaceId}/reports/members");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
