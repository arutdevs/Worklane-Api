using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Multitenancy;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public sealed class InvitationLegacyMigrationTests : IAsyncLifetime
{
    private const string LegacyToken = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string PreviousMigration = "20261025155535_MakeEmailIndexUnique";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("worklane_legacy")
        .WithUsername("worklane")
        .WithPassword("worklane")
        .Build();

    private readonly string _inviteeEmail = $"legacy-{Guid.NewGuid():N}@example.com";
    private readonly Guid _workspaceId = Guid.NewGuid();
    private WorklaneWebApplicationFactory? _factory;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await SeedLegacyInviteAsync();
        _factory = new WorklaneWebApplicationFactory(_postgres.GetConnectionString());
        await _factory.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task LegacyPlaintextInvite_StillAcceptsAfterTheHashBackfill()
    {
        var factory = _factory ?? throw new InvalidOperationException("Factory was not started.");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var stored = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Email == _inviteeEmail);
            stored.TokenHash.Should().Be(InviteTokens.Hash(LegacyToken));
            stored.TokenHash.Should().NotContain(LegacyToken);
            stored.AcceptedAt.Should().BeNull();

            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Invitations'
                """;
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
            names.Should().Contain("TokenHash");
            names.Should().NotContain("Token");
        }

        var invitee = await TestAuthHelper.RegisterAsync(factory.CreateClient(), _inviteeEmail, "Legacy Invitee");
        var accept = await invitee.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(LegacyToken));
        var body = await accept.Content.ReadAsStringAsync();
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent, body);

        var workspaces = await invitee.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        workspaces.Should().Contain(workspace => workspace.Id == _workspaceId && workspace.MyRole == WorkspaceRole.Member);
    }

    private async Task SeedLegacyInviteAsync()
    {
        var options = new DbContextOptionsBuilder<WorklaneDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var db = new WorklaneDbContext(options, new CurrentTenantService());
        await db.Database.MigrateAsync(PreviousMigration);

        var inviterId = Guid.NewGuid();
        var inviterEmail = $"legacy-owner-{Guid.NewGuid():N}@example.com";
        var now = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers" (
                "Id", "DisplayName", "PreferredLanguage", "UserName", "NormalizedUserName",
                "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES (
                {inviterId}, {"Legacy Owner"}, {"en"}, {inviterEmail}, {inviterEmail.ToUpperInvariant()},
                {inviterEmail}, {inviterEmail.ToUpperInvariant()}, {false}, {false},
                {false}, {false}, {0})
            """);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Workspaces" ("Id", "Name", "Slug", "Plan", "CreatedAt")
            VALUES ({_workspaceId}, {"Legacy Workspace"}, {_workspaceId.ToString("N")}, {0}, {now})
            """);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkspaceMembers" ("Id", "WorkspaceId", "UserId", "Role", "JoinedAt")
            VALUES ({Guid.NewGuid()}, {_workspaceId}, {inviterId}, {(int)WorkspaceRole.Owner}, {now})
            """);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Invitations" (
                "Id", "WorkspaceId", "Email", "Role", "Token", "InvitedById", "ExpiresAt", "CreatedAt")
            VALUES (
                {Guid.NewGuid()}, {_workspaceId}, {_inviteeEmail}, {(int)WorkspaceRole.Member}, {LegacyToken},
                {inviterId}, {now.AddDays(7)}, {now})
            """);

        await db.Database.MigrateAsync();
    }
}
