using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Auth.Dtos;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Auth;

public class SignupTransactionTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public SignupTransactionTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IdentityStore_UsesTheSameContextAsRegistration()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IUserStore<AppUser>>();
        var storeContext = store.GetType().GetProperty("Context")?.GetValue(store);

        storeContext.Should().BeSameAs(db);
    }

    [Fact]
    public async Task Register_ConcurrentSameSlug_BothSucceedWithDistinctWorkspaces()
    {
        var gate = _factory.Services.GetRequiredService<WorkspaceInsertGate>();
        var display = "Race" + Guid.NewGuid().ToString("N");
        var emailA = $"race-a-{Guid.NewGuid():N}@example.com";
        var emailB = $"race-b-{Guid.NewGuid():N}@example.com";
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();

        gate.Arm(2);
        HttpResponseMessage first;
        HttpResponseMessage second;
        try
        {
            var pending = await Task.WhenAll(
                clientA.PostAsJsonAsync("/api/auth/register", new RegisterRequest(emailA, "Password123!", display)),
                clientB.PostAsJsonAsync("/api/auth/register", new RegisterRequest(emailB, "Password123!", display)));
            first = pending[0];
            second = pending[1];
        }
        finally
        {
            gate.Disarm();
        }

        var bodyA = await first.Content.ReadAsStringAsync();
        var bodyB = await second.Content.ReadAsStringAsync();
        first.StatusCode.Should().Be(HttpStatusCode.OK, bodyA);
        second.StatusCode.Should().Be(HttpStatusCode.OK, bodyB);
        gate.Arrivals.Should().Be(2);

        var slugA = await WorkspaceSlugOf(clientA, bodyA, $"{display}'s Workspace");
        var slugB = await WorkspaceSlugOf(clientB, bodyB, $"{display}'s Workspace");
        var expected = WorkspaceSlug.ForDisplayName(display);
        new[] { slugA, slugB }.Should().BeEquivalentTo(expected, expected + "-1");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var users = await db.Users.Where(u => u.Email == emailA || u.Email == emailB).ToListAsync();
        users.Should().HaveCount(2);
        var ids = users.Select(u => u.Id).ToArray();
        (await db.WorkspaceMembers.IgnoreQueryFilters().CountAsync(m => ids.Contains(m.UserId))).Should().Be(2);
    }

    [Fact]
    public async Task Register_WhenWorkspaceInsertFails_LeavesNoUser()
    {
        var email = $"orphan-{Guid.NewGuid():N}@example.com";
        var display = "Orphan" + Guid.NewGuid().ToString("N");

        await WithWorkspaceInsertTrigger(
            "wl_signup_orphan",
            "RAISE EXCEPTION 'workspace insert failed' USING ERRCODE = 'check_violation';",
            async db =>
            {
                var client = _factory.CreateClient();
                var response = await client.PostAsJsonAsync(
                    "/api/auth/register",
                    new RegisterRequest(email, "Password123!", display));
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
                using var doc = JsonDocument.Parse(body);
                doc.RootElement.GetProperty("code").GetString().Should().Be("internal_error");
                doc.RootElement.GetProperty("detail").GetString().Should().Be("An unexpected error occurred.");

                (await AttemptsAsync(db, "wl_signup_orphan_seq")).Should().Be(1);
                await AssertNoAccountAsync(db, email, display);
            });

        await RegisterSucceeds(email, display);
    }

    [Fact]
    public async Task Register_WhenSlugRetriesAreExhausted_ReturnsWorkspaceSlugUnavailable()
    {
        var email = $"slug-exhausted-{Guid.NewGuid():N}@example.com";
        var display = "Exhaust" + Guid.NewGuid().ToString("N");

        await WithWorkspaceInsertTrigger(
            "wl_signup_slug",
            """
            RAISE EXCEPTION 'duplicate key value violates unique constraint "IX_Workspaces_Slug"'
              USING ERRCODE = 'unique_violation',
                    CONSTRAINT = 'IX_Workspaces_Slug',
                    TABLE = 'Workspaces',
                    SCHEMA = 'public';
            """,
            async db =>
            {
                var client = _factory.CreateClient();
                var response = await client.PostAsJsonAsync(
                    "/api/auth/register",
                    new RegisterRequest(email, "Password123!", display));
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
                using var doc = JsonDocument.Parse(body);
                doc.RootElement.GetProperty("code").GetString().Should().Be("workspace_slug_unavailable");
                doc.RootElement.GetProperty("detail").GetString().Should().Be(
                    "A unique workspace address could not be reserved. Try again.");

                (await AttemptsAsync(db, "wl_signup_slug_seq")).Should().Be(AuthService.MaxSlugInsertAttempts);
                await AssertNoAccountAsync(db, email, display);
            });

        await RegisterSucceeds(email, display);
    }

    private async Task RegisterSucceeds(string email, string display)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "Password123!", display));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
    }

    private async Task WithWorkspaceInsertTrigger(string name, string raiseSql, Func<WorklaneDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var install = """
            DROP SEQUENCE IF EXISTS {NAME}_seq;
            CREATE SEQUENCE {NAME}_seq;
            CREATE OR REPLACE FUNCTION {NAME}() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
              PERFORM nextval('{NAME}_seq');
              {BODY}
            END;
            $fn$;
            DROP TRIGGER IF EXISTS {NAME} ON "Workspaces";
            CREATE TRIGGER {NAME}
            BEFORE INSERT ON "Workspaces"
            FOR EACH ROW EXECUTE FUNCTION {NAME}();
            """
            .Replace("{NAME}", name, StringComparison.Ordinal)
            .Replace("{BODY}", raiseSql, StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync(install);

        try
        {
            await action(db);
        }
        finally
        {
            var drop = """
                DROP TRIGGER IF EXISTS {NAME} ON "Workspaces";
                DROP FUNCTION IF EXISTS {NAME}();
                DROP SEQUENCE IF EXISTS {NAME}_seq;
                """
                .Replace("{NAME}", name, StringComparison.Ordinal);
            await db.Database.ExecuteSqlRawAsync(drop);
        }
    }

    private static async Task AssertNoAccountAsync(WorklaneDbContext db, string email, string display)
    {
        (await db.Users.AnyAsync(u => u.Email == email)).Should().BeFalse();
        var workspaceName = $"{display}'s Workspace";
        (await db.Workspaces.AnyAsync(w => w.Name == workspaceName)).Should().BeFalse();
    }

    private static async Task<long> AttemptsAsync(WorklaneDbContext db, string sequence)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT last_value, is_called FROM {sequence}";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var last = reader.GetInt64(0);
        var called = reader.GetBoolean(1);
        return called ? last : 0;
    }

    private static async Task<string> WorkspaceSlugOf(HttpClient client, string registerBody, string workspaceName)
    {
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(registerBody, JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var list = await client.GetAsync("/api/workspaces");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        foreach (var workspace in doc.RootElement.EnumerateArray())
        {
            if (workspace.GetProperty("name").GetString() == workspaceName)
                return workspace.GetProperty("slug").GetString()!;
        }

        throw new InvalidOperationException($"No workspace named {workspaceName}.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed class RetryingDatabaseFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MaxRetryCount"] = "3"
            });
        });
    }
}

public class RetryingSignupTests : IClassFixture<RetryingDatabaseFactory>
{
    private readonly RetryingDatabaseFactory _factory;

    public RetryingSignupTests(RetryingDatabaseFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_WhenRetriesAreEnabled_UsesTheExecutionStrategy()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        db.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();

        var client = _factory.CreateClient();
        var email = $"retry-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "Password123!", "Retry User"));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        (await db.Users.AnyAsync(u => u.Email == email)).Should().BeTrue();
        (await db.Workspaces.AnyAsync(w => w.Name == "Retry User's Workspace")).Should().BeTrue();
    }
}
