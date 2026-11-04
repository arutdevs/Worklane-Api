using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Imports;

public class ImportMigrationTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ImportMigrationTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExternalId_IsUniquePerProjectIncludingArchived_AndDownDropsOnlyImportData()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-mig-{Guid.NewGuid():N}@example.com");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var columnId = board.Todo.Id;
        db.TaskItems.Add(Card(board.Project.Id, columnId, "ONE", archived: false));
        await db.SaveChangesAsync();
        db.TaskItems.Add(Card(board.Project.Id, columnId, "ONE", archived: true));
        var duplicate = await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        UniqueConstraintConflicts.Translate(duplicate.Which)!.Code.Should().Be("import_external_id_taken");
        db.ChangeTracker.Clear();

        var other = await board.Client.PostAsJsonAsync(
            $"/api/workspaces/{board.User.Workspace.Id}/projects",
            new { name = "Second", key = "SC" });
        other.EnsureSuccessStatusCode();
        var otherId = (await other.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();
        var otherColumns = await board.Client.GetFromJsonAsync<List<System.Text.Json.JsonElement>>(
            $"/api/projects/{otherId}/columns",
            TestAuthHelper.JsonOptions);
        db.ChangeTracker.Clear();
        db.TaskItems.Add(Card(otherId, otherColumns![0].GetProperty("id").GetGuid(), "ONE", archived: false));
        await db.SaveChangesAsync();

        var bare = Card(board.Project.Id, columnId, "NO-SOURCE", archived: false);
        bare.ImportSource = null;
        db.TaskItems.Add(bare);
        await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_TaskItems_ProjectId_ImportSource_ExternalId'";
            var definition = (string)(await command.ExecuteScalarAsync())!;
            definition.Should().Contain("UNIQUE");
            definition.Should().Contain("ExternalId\" IS NOT NULL");
        }

        var migrator = db.GetService<IMigrator>()!;
        await migrator.MigrateAsync("20261103122429_AddTaskParent");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'TaskItems' AND column_name = 'ExternalId'";
            var count = (long)(await command.ExecuteScalarAsync())!;
            count.Should().Be(0);
        }

        await migrator.MigrateAsync("20261104105330_AddTaskImport");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'TaskItems' AND column_name = 'ExternalId' AND is_nullable = 'YES'";
            var count = (long)(await command.ExecuteScalarAsync())!;
            count.Should().Be(1);
        }
    }

    private static TaskItem Card(Guid projectId, Guid columnId, string externalId, bool archived) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ColumnId = columnId,
        Number = Random.Shared.Next(1000, 100000),
        Title = externalId,
        Priority = TaskPriority.Medium,
        Order = 65536,
        CreatedById = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        IsArchived = archived,
        ImportSource = ImportSource.Generic,
        ExternalId = externalId
    };
}
