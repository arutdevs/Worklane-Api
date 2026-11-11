using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Board;
using Worklane.Application.Board.Dtos;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class ArchiveTimestampTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private const string PreviousMigration = "20261103122429_AddTaskParent";

    private readonly WorklaneWebApplicationFactory _factory;

    public ArchiveTimestampTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LiveTask_SerializesArchivedAtNull()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "archive-at-live@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Still here");

        var body = await board.Client.GetStringAsync($"/api/tasks/{task.Id}");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("isArchived").GetBoolean().Should().BeFalse();

        var dto = JsonSerializer.Deserialize<TaskItemDto>(body, TestAuthHelper.JsonOptions);
        dto!.ArchivedAt.Should().BeNull();
        dto.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Archive_SetsUtcTimestampAndOneActivity_RepeatIsNotFound()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "archive-at-one@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Leaving");
        var before = DateTimeOffset.UtcNow;

        var archived = await board.Client.DeleteAsync($"/api/tasks/{task.Id}");
        archived.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stored = await LoadTask(task.Id, board.User.Workspace.Id);
        stored.IsArchived.Should().BeTrue();
        stored.ArchivedAt.Should().NotBeNull();
        stored.ArchivedAt.Should().Be(stored.UpdatedAt);
        stored.ArchivedAt!.Value.Should().BeOnOrAfter(before).And.BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        var activity = await LoadArchivedActivity(task.Id, board.User.Workspace.Id);
        activity.Should().ContainSingle();
        activity[0].ActorUserId.Should().Be(board.User.Tokens.User.Id);
        activity[0].CreatedAt.Should().Be(stored.ArchivedAt.Value);

        (await board.Client.GetAsync($"/api/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var again = await board.Client.DeleteAsync($"/api/tasks/{task.Id}");
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadTask(task.Id, board.User.Workspace.Id)).ArchivedAt.Should().Be(stored.ArchivedAt);
        (await LoadArchivedActivity(task.Id, board.User.Workspace.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Archive_ParentCascadeStampsEachNewCardAndLeavesTheOldChild()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "archive-at-cascade@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var first = await ReadTask(await PostChild(board, "One", parent.Id));
        var second = await ReadTask(await PostChild(board, "Two", parent.Id));
        var older = await ReadTask(await PostChild(board, "Older", parent.Id));
        (await board.Client.DeleteAsync($"/api/tasks/{older.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var past = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await SetArchivedAt(older.Id, board.User.Workspace.Id, past);

        var blocked = await board.Client.DeleteAsync($"/api/tasks/{parent.Id}");
        await AssertProblem(blocked, HttpStatusCode.Conflict, ParentTaskErrors.ParentHasActiveChildren);
        (await LoadTask(parent.Id, board.User.Workspace.Id)).ArchivedAt.Should().BeNull();

        var archived = await board.Client.DeleteAsync($"/api/tasks/{parent.Id}?includeChildren=true");
        archived.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await LoadTasks([parent.Id, first.Id, second.Id, older.Id], board.User.Workspace.Id);
        var stamped = rows.Where(task => task.Id != older.Id).ToList();
        stamped.Should().OnlyContain(task => task.IsArchived && task.ArchivedAt == stamped[0].ArchivedAt);
        stamped[0].ArchivedAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        rows.Single(task => task.Id == older.Id).ArchivedAt.Should().Be(past);

        foreach (var task in stamped)
        {
            var history = await LoadArchivedActivity(task.Id, board.User.Workspace.Id);
            history.Should().ContainSingle();
            history[0].ActorUserId.Should().Be(board.User.Tokens.User.Id);
            history[0].CreatedAt.Should().Be(task.ArchivedAt!.Value);
        }

        (await LoadArchivedActivity(older.Id, board.User.Workspace.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Archive_RejectedRequestsLeaveTimestampAndActivityUntouched()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "archive-at-reject@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var child = await ReadTask(await PostChild(board, "Child", parent.Id));
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "archive-at-viewer@test.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "archive-at-out@test.com");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, viewer.Tokens.User.Email, "Viewer");

        await AssertProblem(
            await board.Client.DeleteAsync($"/api/tasks/{parent.Id}"),
            HttpStatusCode.Conflict,
            ParentTaskErrors.ParentHasActiveChildren);
        (await viewer.Client.DeleteAsync($"/api/tasks/{parent.Id}?includeChildren=true")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.Client.DeleteAsync($"/api/tasks/{child.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.Client.GetAsync($"/api/tasks/{child.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await board.Client.DeleteAsync($"/api/tasks/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        foreach (var id in new[] { parent.Id, child.Id })
        {
            var stored = await LoadTask(id, board.User.Workspace.Id);
            stored.IsArchived.Should().BeFalse();
            stored.ArchivedAt.Should().BeNull();
            (await LoadArchivedActivity(id, board.User.Workspace.Id)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Migration_BackfillsArchivedRowsOnly()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "archive-at-migrate@test.com");
        var live = await BoardTestSetup.CreateTaskAsync(board, "Live");
        var archived = await BoardTestSetup.CreateTaskAsync(board, "Old archive");
        (await board.Client.DeleteAsync($"/api/tasks/{archived.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var migrator = db.GetService<IMigrator>() ?? throw new InvalidOperationException("IMigrator was not registered.");
        var migrationId = db.Database.GetMigrations().Single(name => name.EndsWith("_AddTaskArchivedAt", StringComparison.Ordinal));
        var up = migrator.GenerateScript(PreviousMigration, migrationId);
        up.Should().Contain("ADD \"ArchivedAt\"");
        up.Should().Contain("UPDATE \"TaskItems\"");
        up.Should().Contain("\"IsArchived\" = TRUE");
        up.Should().NotContain("DELETE FROM \"TaskItems\"");
        var down = migrator.GenerateScript(migrationId, PreviousMigration);
        down.Should().Contain("DROP COLUMN \"ArchivedAt\"");
        down.Should().NotContain("DELETE FROM \"TaskItems\"");

        var past = new DateTimeOffset(2019, 5, 6, 7, 8, 9, TimeSpan.Zero);
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "TaskItems" SET "UpdatedAt" = {past} WHERE "Id" = {archived.Id}""");
            await migrator.MigrateAsync();
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        var archivedRow = await LoadTask(archived.Id, board.User.Workspace.Id);
        archivedRow.Title.Should().Be("Old archive");
        archivedRow.IsArchived.Should().BeTrue();
        archivedRow.UpdatedAt.Should().Be(past);
        archivedRow.ArchivedAt.Should().Be(past);

        var liveRow = await LoadTask(live.Id, board.User.Workspace.Id);
        liveRow.Title.Should().Be("Live");
        liveRow.IsArchived.Should().BeFalse();
        liveRow.ArchivedAt.Should().BeNull();

        var body = await board.Client.GetStringAsync($"/api/tasks/{live.Id}");
        JsonDocument.Parse(body).RootElement.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task<TaskItem> LoadTask(Guid taskId, Guid workspaceId)
    {
        var rows = await LoadTasks([taskId], workspaceId);
        return rows.Single();
    }

    private async Task<List<TaskItem>> LoadTasks(IReadOnlyCollection<Guid> taskIds, Guid workspaceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.TaskItems.IgnoreQueryFilters()
            .Where(task => taskIds.Contains(task.Id) && task.Project.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    private async Task<List<TaskActivity>> LoadArchivedActivity(Guid taskId, Guid workspaceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.TaskActivities.IgnoreQueryFilters()
            .Where(activity =>
                activity.TaskId == taskId &&
                activity.Type == TaskActivityType.Archived &&
                activity.Task.Project.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    private async Task SetArchivedAt(Guid taskId, Guid workspaceId, DateTimeOffset archivedAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var task = await db.TaskItems.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == taskId && row.Project.WorkspaceId == workspaceId);
        task.ArchivedAt = archivedAt;
        await db.SaveChangesAsync();
    }

    private static async Task<TaskItemDto> ReadTask(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static Task<HttpResponseMessage> PostChild(BoardContext board, string title, Guid parentId) =>
        board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new { title, priority = "Medium", parentTaskId = parentId });

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }
}
