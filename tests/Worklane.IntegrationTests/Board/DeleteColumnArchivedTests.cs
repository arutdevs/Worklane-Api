using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class DeleteColumnArchivedTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public DeleteColumnArchivedTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EmptyColumn_Is204_EvenWhenTheTargetDoesNotExist()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-empty@test.com");
        var extra = await CreateColumn(board, "Empty");

        var withBogusTarget = await DeleteColumn(board.Client, extra, Guid.NewGuid());
        withBogusTarget.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var emptyDefault = await DeleteColumn(board.Client, board.Done.Id);
        emptyDefault.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var columns = await Columns(board);
        columns.Should().NotContain(c => c.Id == extra);
        columns.Should().NotContain(c => c.Id == board.Done.Id);
    }

    [Fact]
    public async Task ArchivedOnly_WithoutTarget_Is409AndKeepsTheColumn()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-archived-only@test.com");
        var first = await BoardTestSetup.CreateTaskAsync(board, "Old one", board.Done.Id);
        var second = await BoardTestSetup.CreateTaskAsync(board, "Old two", board.Done.Id);
        await Archive(board.Client, first.Id);
        await Archive(board.Client, second.Id);

        var response = await DeleteColumn(board.Client, board.Done.Id);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("column_has_archived_cards");
        problem.GetProperty("detail").GetString().Should().Be("Column has 2 archived card(s). Choose a column to move them to.");
        problem.GetProperty("activeTaskCount").GetInt32().Should().Be(0);
        problem.GetProperty("archivedTaskCount").GetInt32().Should().Be(2);

        (await Columns(board)).Should().Contain(c => c.Id == board.Done.Id);
        (await StoredTask(first.Id)).ColumnId.Should().Be(board.Done.Id);
    }

    [Fact]
    public async Task ArchivedOnly_WithTarget_MovesCardAndKeepsChildren()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-move-archived@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Filed away", board.Done.Id);
        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "keep me" }))
            .EnsureSuccessStatusCode();
        (await Upload(board.Client, task.Id, SampleFiles.Png, "shot.png")).EnsureSuccessStatusCode();
        await Archive(board.Client, task.Id);

        var response = await DeleteColumn(board.Client, board.Done.Id, board.Todo.Id);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stored = await StoredTask(task.Id);
        stored.ColumnId.Should().Be(board.Todo.Id);
        stored.IsArchived.Should().BeTrue();
        stored.Title.Should().Be("Filed away");

        var children = await ChildCounts(task.Id);
        children.Comments.Should().Be(1);
        children.Attachments.Should().Be(1);

        var columns = await Columns(board);
        columns.Should().NotContain(c => c.Id == board.Done.Id);
        columns.Single(c => c.Id == board.Todo.Id).Tasks.Should().NotContain(t => t.Id == task.Id);
        columns.Single(c => c.Id == board.Todo.Id).ArchivedTaskCount.Should().Be(1);
    }

    [Fact]
    public async Task MixedCards_WithTarget_AppendsInExistingOrder()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-mixed@test.com");
        var parked = await BoardTestSetup.CreateTaskAsync(board, "Parked", board.InProgress.Id);
        await Archive(board.Client, parked.Id);
        await SetOrder(parked.Id, 5_000_000);
        var stay = await BoardTestSetup.CreateTaskAsync(board, "Stay", board.InProgress.Id);

        var activeFirst = await BoardTestSetup.CreateTaskAsync(board, "A", board.Done.Id);
        var archived = await BoardTestSetup.CreateTaskAsync(board, "B", board.Done.Id);
        var activeLast = await BoardTestSetup.CreateTaskAsync(board, "C", board.Done.Id);
        await Archive(board.Client, archived.Id);

        var response = await DeleteColumn(board.Client, board.Done.Id, board.InProgress.Id);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var movedA = await StoredTask(activeFirst.Id);
        var movedB = await StoredTask(archived.Id);
        var movedC = await StoredTask(activeLast.Id);
        var stayed = await StoredTask(stay.Id);
        movedA.ColumnId.Should().Be(board.InProgress.Id);
        movedB.ColumnId.Should().Be(board.InProgress.Id);
        movedC.ColumnId.Should().Be(board.InProgress.Id);
        movedA.IsArchived.Should().BeFalse();
        movedB.IsArchived.Should().BeTrue();
        movedC.IsArchived.Should().BeFalse();
        stayed.Order.Should().BeLessThan(movedA.Order);
        movedA.Order.Should().BeGreaterThan(5_000_000);
        movedA.Order.Should().BeLessThan(movedB.Order);
        movedB.Order.Should().BeLessThan(movedC.Order);

        var visible = (await Columns(board)).Single(c => c.Id == board.InProgress.Id).Tasks.Select(t => t.Title).ToList();
        visible.Should().Equal("Stay", "A", "C");
    }

    [Fact]
    public async Task LastColumn_WithArchivedCards_Is409()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-last@test.com");
        var archived = await BoardTestSetup.CreateTaskAsync(board, "Last archived", board.Todo.Id);
        await Archive(board.Client, archived.Id);
        (await DeleteColumn(board.Client, board.InProgress.Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DeleteColumn(board.Client, board.Done.Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await DeleteColumn(board.Client, board.Todo.Id);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("last_column_has_archived_cards");
        problem.GetProperty("detail").GetString().Should().Be(
            "This is the project's last column and it has 1 archived card(s). Purge them or add another column first.");
        problem.GetProperty("archivedTaskCount").GetInt32().Should().Be(1);
        problem.TryGetProperty("activeTaskCount", out _).Should().BeFalse();
        (await Columns(board)).Should().ContainSingle(c => c.Id == board.Todo.Id);
    }

    [Fact]
    public async Task LastColumn_WithActiveAndArchived_UsesLastColumnCode()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-last-mixed@test.com");
        await BoardTestSetup.CreateTaskAsync(board, "Still open", board.Todo.Id);
        var archived = await BoardTestSetup.CreateTaskAsync(board, "Also old", board.Todo.Id);
        await Archive(board.Client, archived.Id);
        (await DeleteColumn(board.Client, board.InProgress.Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DeleteColumn(board.Client, board.Done.Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await DeleteColumn(board.Client, board.Todo.Id);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("last_column_has_archived_cards");
        problem.GetProperty("archivedTaskCount").GetInt32().Should().Be(1);
        problem.GetProperty("activeTaskCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ActiveCards_WithoutTarget_Is409WithBothCounts()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-active@test.com");
        await BoardTestSetup.CreateTaskAsync(board, "Open", board.Done.Id);
        var archived = await BoardTestSetup.CreateTaskAsync(board, "Closed", board.Done.Id);
        await Archive(board.Client, archived.Id);

        var response = await DeleteColumn(board.Client, board.Done.Id);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("column_has_active_cards");
        problem.GetProperty("detail").GetString().Should().Be(
            "Column has 1 card(s) and 1 archived card(s). Provide targetColumnId to move them first.");
        problem.GetProperty("activeTaskCount").GetInt32().Should().Be(1);
        problem.GetProperty("archivedTaskCount").GetInt32().Should().Be(1);
        (await Columns(board)).Should().Contain(c => c.Id == board.Done.Id);
    }

    [Fact]
    public async Task TargetInAnotherProjectOrWorkspace_Is404AndMovesNothing()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-foreign@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Stay", board.Done.Id);
        await Archive(board.Client, task.Id);

        var otherProjectResponse = await board.Client.PostAsJsonAsync(
            $"/api/workspaces/{board.User.Workspace.Id}/projects",
            new { name = "Other", key = "OT" });
        otherProjectResponse.EnsureSuccessStatusCode();
        var otherProject = (await otherProjectResponse.Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions))!;
        var otherColumns = await (await board.Client.GetAsync($"/api/projects/{otherProject.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var sameWorkspaceColumn = otherColumns!.Single(c => c.Name == "To Do").Id;

        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "wl60-foreign-out@test.com");
        var outsiderProject = await outsider.Client.PostAsJsonAsync(
            $"/api/workspaces/{outsider.Workspace.Id}/projects",
            new { name = "Elsewhere", key = "EW" });
        outsiderProject.EnsureSuccessStatusCode();
        var elsewhere = (await outsiderProject.Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions))!;
        var elsewhereColumns = await (await outsider.Client.GetAsync($"/api/projects/{elsewhere.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var otherWorkspaceColumn = elsewhereColumns!.Single(c => c.Name == "To Do").Id;

        foreach (var foreignId in new[] { sameWorkspaceColumn, otherWorkspaceColumn })
        {
            var response = await DeleteColumn(board.Client, board.Done.Id, foreignId);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
            var problem = JsonDocument.Parse(body).RootElement;
            problem.GetProperty("code").GetString().Should().Be("target_column_not_found");
            problem.GetProperty("detail").GetString().Should().Be("Target column was not found in this project.");
            body.Should().NotContain(foreignId.ToString());
        }

        var stored = await StoredTask(task.Id);
        stored.ColumnId.Should().Be(board.Done.Id);
        stored.IsArchived.Should().BeTrue();
        (await Columns(board)).Should().Contain(c => c.Id == board.Done.Id);
    }

    [Fact]
    public async Task SameColumnTarget_Is400()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-same@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Loop", board.Done.Id);
        await Archive(board.Client, task.Id);

        var response = await DeleteColumn(board.Client, board.Done.Id, board.Done.Id);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("target_column_same_as_source");
        problem.GetProperty("detail").GetString().Should().Be("Target column must be different from the column being deleted.");
        (await StoredTask(task.Id)).ColumnId.Should().Be(board.Done.Id);
    }

    [Fact]
    public async Task OtherWorkspaceUser_Is403AndColumnRemains()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-tenant@test.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "wl60-tenant-out@test.com");

        var response = await DeleteColumn(outsider.Client, board.Done.Id);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be("workspace_access_denied");
        (await Columns(board)).Should().Contain(c => c.Id == board.Done.Id);
    }

    [Fact]
    public async Task ForeignKeyConflictOnDelete_Is409AndRollsBack()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-race@test.com");
        var columnId = await CreateColumn(board, "RaceColumn");
        var marker = "race-" + Guid.NewGuid().ToString("N");
        var number = Random.Shared.Next(100_000_000, int.MaxValue);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION wl60_race_column() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
              IF OLD."Name" = 'RaceColumn' THEN
                INSERT INTO "TaskItems"
                  ("Id", "ProjectId", "ColumnId", "Number", "Title", "Priority", "Order", "CreatedById", "CreatedAt", "UpdatedAt", "IsArchived")
                VALUES
                  (gen_random_uuid(), OLD."ProjectId", OLD."Id", {NUMBER}, '{TITLE}', 'Medium', 1, '{USER}', now(), now(), false);
              END IF;
              RETURN OLD;
            END;
            $fn$;
            DROP TRIGGER IF EXISTS wl60_race_column ON "BoardColumns";
            CREATE TRIGGER wl60_race_column
            BEFORE DELETE ON "BoardColumns"
            FOR EACH ROW EXECUTE FUNCTION wl60_race_column();
            """
            .Replace("{NUMBER}", number.ToString(), StringComparison.Ordinal)
            .Replace("{TITLE}", marker, StringComparison.Ordinal)
            .Replace("{USER}", board.User.Tokens.User.Id.ToString(), StringComparison.Ordinal));

        try
        {
            var response = await DeleteColumn(board.Client, columnId);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
            var problem = JsonDocument.Parse(body).RootElement;
            problem.GetProperty("code").GetString().Should().Be("column_delete_conflict");
            problem.GetProperty("detail").GetString().Should().Be(
                "The column changed while it was being deleted. Reload the board and try again.");
            problem.TryGetProperty("activeTaskCount", out _).Should().BeFalse();
            problem.TryGetProperty("archivedTaskCount", out _).Should().BeFalse();

            (await db.BoardColumns.IgnoreQueryFilters().AnyAsync(c => c.Id == columnId)).Should().BeTrue();
            (await db.TaskItems.IgnoreQueryFilters().AnyAsync(t => t.Title == marker)).Should().BeFalse();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS wl60_race_column ON "BoardColumns";
                DROP FUNCTION IF EXISTS wl60_race_column();
                """);
        }
    }

    [Fact]
    public async Task ColumnDto_ReportsArchivedTaskCount()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "wl60-count@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Hidden", board.Todo.Id);
        var visible = await BoardTestSetup.CreateTaskAsync(board, "Shown", board.Todo.Id);
        await Archive(board.Client, task.Id);

        var columns = await Columns(board);
        var todo = columns.Single(c => c.Id == board.Todo.Id);
        todo.ArchivedTaskCount.Should().Be(1);
        todo.TaskCount.Should().Be(1);
        todo.Tasks.Select(t => t.Id).Should().Equal(visible.Id);
        columns.Single(c => c.Id == board.Done.Id).ArchivedTaskCount.Should().Be(0);

        var renamed = await board.Client.PatchAsJsonAsync($"/api/columns/{board.Todo.Id}", new { name = "Inbox" });
        renamed.EnsureSuccessStatusCode();
        var updated = (await renamed.Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions))!;
        updated.ArchivedTaskCount.Should().Be(1);
        updated.Name.Should().Be("Inbox");
    }

    private async Task<List<BoardColumnDto>> Columns(BoardContext board)
    {
        var response = await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<Guid> CreateColumn(BoardContext board, string name)
    {
        var response = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/columns", new { name });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<BoardColumnDto>(TestAuthHelper.JsonOptions))!;
        created.ArchivedTaskCount.Should().Be(0);
        return created.Id;
    }

    private static Task<HttpResponseMessage> DeleteColumn(HttpClient client, Guid columnId, Guid? targetColumnId = null)
    {
        var url = targetColumnId is Guid target
            ? $"/api/columns/{columnId}?targetColumnId={target}"
            : $"/api/columns/{columnId}";
        return client.DeleteAsync(url);
    }

    private static async Task Archive(HttpClient client, Guid taskId)
    {
        var response = await client.DeleteAsync($"/api/tasks/{taskId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, Guid taskId, byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return client.PostAsync($"/api/tasks/{taskId}/attachments", content);
    }

    private async Task<TaskItem> StoredTask(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.TaskItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    private async Task SetOrder(Guid taskId, double order)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var task = await db.TaskItems.IgnoreQueryFilters().SingleAsync(t => t.Id == taskId);
        task.Order = order;
        await db.SaveChangesAsync();
    }

    private async Task<(int Comments, int Attachments)> ChildCounts(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var comments = await db.TaskComments.IgnoreQueryFilters().CountAsync(c => c.TaskId == taskId);
        var attachments = await db.TaskAttachments.IgnoreQueryFilters().CountAsync(a => a.TaskId == taskId);
        return (comments, attachments);
    }
}
