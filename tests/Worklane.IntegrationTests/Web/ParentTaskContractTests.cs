using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worklane.Application.Board;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Multitenancy;
using Worklane.Infrastructure.Persistence;
using Worklane.WebApi.Middleware;

namespace Worklane.IntegrationTests.Web;

public class ParentTaskContractTests
{
    [Fact]
    public void Model_HasParentLinkConstraintCheckAndPartialIndex()
    {
        var options = new DbContextOptionsBuilder<WorklaneDbContext>()
            .UseNpgsql("Host=localhost;Database=worklane")
            .Options;
        using var db = new WorklaneDbContext(options, new CurrentTenantService());
        var entity = db.Model.FindEntityType(typeof(TaskItem));
        entity.Should().NotBeNull();

        var link = entity!.GetForeignKeys().Single(fk =>
            fk.Properties.Select(property => property.Name).SequenceEqual(["ProjectId", "ParentTaskId"]));
        link.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        link.PrincipalEntityType.ClrType.Should().Be(typeof(TaskItem));

        var index = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == "IX_TaskItems_ProjectId_ParentTaskId");
        index.IsUnique.Should().BeFalse();
        index.GetFilter().Should().Be("\"ParentTaskId\" IS NOT NULL");

        entity.GetKeys().Should().Contain(key => key.GetName() == "AK_TaskItems_ProjectId_Id");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.CheckViolation, ParentLinkConflicts.CheckConstraint, HttpStatusCode.BadRequest, ParentTaskErrors.ParentSelf)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, ParentLinkConflicts.ForeignKeyConstraint, HttpStatusCode.Conflict, ParentTaskErrors.ParentHasChildren)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, null, HttpStatusCode.Conflict, ParentTaskErrors.ParentLinkConflict)]
    [InlineData(PostgresErrorCodes.SerializationFailure, null, HttpStatusCode.Conflict, ParentTaskErrors.ParentLinkConflict)]
    public async Task Middleware_MapsParentLinkFailures(string sqlState, string? constraint, HttpStatusCode status, string code)
    {
        var postgres = new PostgresException(
            "database message that must not leak",
            "ERROR",
            "ERROR",
            sqlState,
            constraintName: constraint);
        var body = await Invoke(new DbUpdateException("SaveChanges failed", postgres));

        body.Status.Should().Be(status);
        body.Json.GetProperty("code").GetString().Should().Be(code);
        var raw = body.Json.GetRawText();
        raw.Should().NotContain("database message");
        raw.Should().NotContain(sqlState);
        if (constraint is not null)
            raw.Should().NotContain(constraint);
        if (code == ParentTaskErrors.ParentHasChildren)
            body.Json.TryGetProperty(ParentTaskErrors.ChildCount, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Middleware_LeavesADifferentForeignKeyUnmapped()
    {
        var postgres = new PostgresException(
            "insert or update violates foreign key constraint \"FK_TaskItems_BoardColumns_ColumnId\"",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.ForeignKeyViolation,
            constraintName: "FK_TaskItems_BoardColumns_ColumnId");
        var body = await Invoke(new DbUpdateException("SaveChanges failed", postgres));

        body.Status.Should().Be(HttpStatusCode.InternalServerError);
        body.Json.GetProperty("code").GetString().Should().Be("internal_error");
        body.Json.GetRawText().Should().NotContain("FK_TaskItems_BoardColumns_ColumnId");
    }

    [Fact]
    public void Catalog_ListsEveryParentCode()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "api-error-codes.md")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        var doc = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "api-error-codes.md"));

        doc.Should().Contain("| `parent_self` | 400 | | A card can't be its own subtask. | emitted |");
        doc.Should().Contain("| `parent_different_project` | 409 | | You can only link cards in the same project. | emitted |");
        doc.Should().Contain("| `parent_depth_exceeded` | 409 | | A subtask can't have its own subtasks. | emitted |");
        doc.Should().Contain("| `child_has_children` | 409 | `childCount` | This card has subtasks, so it can't become a subtask. | emitted |");
        doc.Should().Contain("| `parent_child_limit` | 409 | `maxChildren` | This card already has the maximum of {maxChildren} subtasks. | emitted |");
        doc.Should().Contain("| `already_has_parent` | 409 | `parentDisplayKey` | This card is already a subtask of {parentDisplayKey}. Unlink it first. | emitted |");
        doc.Should().Contain("| `card_archived` | 409 | | Archived cards can't be linked. | emitted |");
        doc.Should().Contain("| `parent_has_active_children` | 409 | `childCount` | This card has {childCount} active subtasks. | emitted |");
        doc.Should().Contain("| `parent_has_children` | 409 | `childCount` | Delete or unlink the subtasks before deleting this card permanently. | emitted |");
        doc.Should().Contain("| `parent_archived` | 409 | | Restore the parent card first. | planned (WL-PARENT) |");
        doc.Should().Contain("| `parent_link_conflict` | 409 | | Someone changed this card at the same time. Reload and try again. | emitted |");
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> Invoke(Exception exception)
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = "/api/tasks/example/parent";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, document.RootElement.Clone());
    }
}
