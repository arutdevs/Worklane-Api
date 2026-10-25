using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Multitenancy;
using Worklane.Infrastructure.Persistence;
using Worklane.WebApi.Middleware;

namespace Worklane.IntegrationTests.Web;

public class UniqueConstraintConflictTests
{
    [Theory]
    [MemberData(nameof(MappedConstraints))]
    public async Task Middleware_MapsEachUniqueIndexToItsCode(string constraint, string code, string detail)
    {
        const string leaked = "duplicate key value violates unique constraint secret-row";
        var postgres = new PostgresException(
            leaked,
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: constraint);
        var body = await Invoke(new DbUpdateException("SaveChanges failed", postgres));

        body.Status.Should().Be(HttpStatusCode.Conflict);
        body.Json.GetProperty("code").GetString().Should().Be(code);
        body.Json.GetProperty("title").GetString().Should().Be("Conflict");
        body.Json.GetProperty("detail").GetString().Should().Be(detail);
        var raw = body.Json.GetRawText();
        raw.Should().NotContain(constraint);
        raw.Should().NotContain(leaked);
        raw.Should().NotContain("23505");
    }

    [Fact]
    public async Task Middleware_MapsAnUnknownUniqueViolationWithoutTheConstraintName()
    {
        var postgres = new PostgresException(
            "duplicate key value violates unique constraint \"PK_TaskItems\"",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: "PK_TaskItems");
        var body = await Invoke(new DbUpdateException("SaveChanges failed", postgres));

        body.Status.Should().Be(HttpStatusCode.Conflict);
        body.Json.GetProperty("code").GetString().Should().Be(UniqueConstraintConflicts.UniqueConflictCode);
        body.Json.GetProperty("detail").GetString().Should().Be(UniqueConstraintConflicts.UniqueConflictDetail);
        body.Json.GetRawText().Should().NotContain("PK_TaskItems");
    }

    [Fact]
    public void Model_MapsEveryUniqueIndex_AndLeavesColumnOrderNonUnique()
    {
        var options = new DbContextOptionsBuilder<WorklaneDbContext>()
            .UseNpgsql("Host=localhost;Database=worklane")
            .Options;
        using var db = new WorklaneDbContext(options, new CurrentTenantService());

        var unique = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            foreach (var index in entity.GetIndexes())
            {
                if (!index.IsUnique)
                    continue;
                var name = index.GetDatabaseName();
                name.Should().NotBeNullOrWhiteSpace();
                unique.Add(name!);
            }
        }

        unique.Should().OnlyContain(name => UniqueConstraintConflicts.Known.ContainsKey(name));
        unique.Should().Contain([
            "IX_Projects_WorkspaceId_Key",
            "IX_WorkspaceMembers_WorkspaceId_UserId",
            "IX_Invitations_Token",
            "IX_ProjectMembers_ProjectId_UserId",
            "IX_RefreshTokens_TokenHash",
            "IX_TaskAttachments_StorageKey",
            "IX_TaskItems_ProjectId_Number",
            "IX_Workspaces_Slug",
            "EmailIndex",
            "UserNameIndex",
            "RoleNameIndex"
        ]);

        var columns = db.Model.FindEntityType(typeof(BoardColumn));
        columns.Should().NotBeNull();
        columns!.GetIndexes().Should().Contain(index =>
            index.GetDatabaseName() == "IX_BoardColumns_ProjectId_Order" && !index.IsUnique);

        var email = db.Model.FindEntityType(typeof(Worklane.Infrastructure.Identity.AppUser))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "EmailIndex");
        email.IsUnique.Should().BeTrue();
    }

    public static IEnumerable<object[]> MappedConstraints() =>
        UniqueConstraintConflicts.Known.Select(pair => new object[] { pair.Key, pair.Value.Code, pair.Value.Detail });

    private static async Task<(HttpStatusCode Status, JsonElement Json)> Invoke(Exception exception)
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = "/api/example";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, document.RootElement.Clone());
    }
}
