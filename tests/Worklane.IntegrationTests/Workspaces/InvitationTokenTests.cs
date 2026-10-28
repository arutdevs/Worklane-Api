using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class InvitationTokenTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public InvitationTokenTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Accept_ValidToken_AddsTheMember_AndTheDatabaseNeverStoresTheRawToken()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var (token, link) = await InvitePendingAsync(owner, email, WorkspaceRole.Member);

        InviteTokens.ByteLength.Should().BeGreaterThanOrEqualTo(32);
        token.Should().HaveLength(InviteTokens.ByteLength * 2);
        link.Should().Be(InviteLinks.ForToken("http://web.test", token));

        await AssertRawTokenIsAbsentAsync(email, token);

        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);
        var accept = await AcceptAsync(invitee, token);

        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertMemberAsync(invitee, owner.Workspace.Id, WorkspaceRole.Member);
        await AssertRawTokenIsAbsentAsync(email, token);
    }

    [Fact]
    public async Task Accept_WrongToken_ReturnsTheSameBodyAsAnUnknownToken()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var (token, _) = await InvitePendingAsync(owner, email, WorkspaceRole.Member);
        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);

        var unknown = await AcceptAsync(invitee, NewToken());
        var wrong = await AcceptAsync(invitee, NewToken());

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        var wrongBody = await wrong.Content.ReadAsStringAsync();
        wrongBody.Should().Be(unknownBody);
        unknownBody.Should().Contain("\"code\":\"not_found\"");
        unknownBody.Should().Contain("Invitation is invalid or has expired.");
        unknownBody.Should().NotContain(token);
        wrongBody.Should().NotContain(token);

        await AssertMemberAsync(invitee, owner.Workspace.Id, null);
    }

    [Fact]
    public async Task Accept_ExpiredOrUsedInvite_IsRejectedWithTheUnknownTokenBody()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var expiredEmail = $"expired-{Guid.NewGuid():N}@example.com";
        var usedEmail = $"used-{Guid.NewGuid():N}@example.com";
        var (expiredToken, _) = await InvitePendingAsync(owner, expiredEmail, WorkspaceRole.Member);
        var (usedToken, _) = await InvitePendingAsync(owner, usedEmail, WorkspaceRole.Viewer);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Email == expiredEmail);
            invitation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var expiredUser = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), expiredEmail);
        var usedUser = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), usedEmail);
        var unknownBody = await (await AcceptAsync(expiredUser, NewToken())).Content.ReadAsStringAsync();

        var expired = await AcceptAsync(expiredUser, expiredToken);
        var usedOnce = await AcceptAsync(usedUser, usedToken);
        var usedAgain = await AcceptAsync(usedUser, usedToken);

        expired.StatusCode.Should().Be(HttpStatusCode.NotFound);
        usedOnce.StatusCode.Should().Be(HttpStatusCode.NoContent);
        usedAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await expired.Content.ReadAsStringAsync()).Should().Be(unknownBody);
        (await usedAgain.Content.ReadAsStringAsync()).Should().Be(unknownBody);
        await AssertMemberAsync(expiredUser, owner.Workspace.Id, null);
        await AssertMemberAsync(usedUser, owner.Workspace.Id, WorkspaceRole.Viewer);
    }

    [Fact]
    public async Task Accept_WrongAccount_MatchesTheUnknownTokenBody_AndDoesNotJoin()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var (token, _) = await InvitePendingAsync(owner, email, WorkspaceRole.Member);
        var other = await TestAuthHelper.RegisterAsync(_factory.CreateClient());

        var unknown = await AcceptAsync(other, NewToken());
        var wrong = await AcceptAsync(other, token);
        var expiredOwner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var expiredEmail = $"expired-{Guid.NewGuid():N}@example.com";
        var (expiredToken, _) = await InvitePendingAsync(expiredOwner, expiredEmail, WorkspaceRole.Member);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Email == expiredEmail);
            invitation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var expiredUser = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), expiredEmail);
        var expired = await AcceptAsync(expiredUser, expiredToken);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        expired.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        (await wrong.Content.ReadAsStringAsync()).Should().Be(unknownBody);
        (await expired.Content.ReadAsStringAsync()).Should().Be(unknownBody);
        unknownBody.Should().Contain("\"code\":\"not_found\"");
        unknownBody.Should().NotContain(token);
        unknownBody.Should().NotContain("different email");

        await AssertMemberAsync(other, owner.Workspace.Id, null);
        await AssertMemberAsync(expiredUser, expiredOwner.Workspace.Id, null);

        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);
        (await AcceptAsync(invitee, token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertMemberAsync(invitee, owner.Workspace.Id, WorkspaceRole.Member);
        await AssertMemberAsync(invitee, other.Workspace.Id, null);
    }

    [Fact]
    public async Task Invite_ExistingAccount_MatchesANewEmail_AndJoinsOnlyOnAccept()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var existing = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var freshEmail = $"fresh-{Guid.NewGuid():N}@example.com";

        var existingResponse = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(existing.Tokens.User.Email, WorkspaceRole.Member));
        var freshResponse = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(freshEmail, WorkspaceRole.Member));
        var existingText = await existingResponse.Content.ReadAsStringAsync();
        var freshText = await freshResponse.Content.ReadAsStringAsync();
        existingResponse.StatusCode.Should().Be(HttpStatusCode.OK, existingText);
        freshResponse.StatusCode.Should().Be(HttpStatusCode.OK, freshText);

        using var existingJson = JsonDocument.Parse(existingText);
        using var freshJson = JsonDocument.Parse(freshText);
        PropertyNames(existingJson).Should().Equal(PropertyNames(freshJson));
        existingJson.RootElement.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        freshJson.RootElement.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        existingJson.RootElement.GetProperty("inviteLink").GetString()
            .Should().StartWith("http://web.test/invitations/accept?token=");
        freshJson.RootElement.GetProperty("inviteLink").GetString()
            .Should().StartWith("http://web.test/invitations/accept?token=");

        await AssertMemberAsync(existing, owner.Workspace.Id, null);

        var token = TestAuthHelper.TokenFromInviteLink(existingJson.RootElement.GetProperty("inviteLink").GetString()!);
        (await AcceptAsync(existing, token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertMemberAsync(existing, owner.Workspace.Id, WorkspaceRole.Member);

        var again = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(existing.Tokens.User.Email, WorkspaceRole.Member));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Accept_TokenCannotJoinAnotherWorkspace()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var other = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var (token, _) = await InvitePendingAsync(owner, email, WorkspaceRole.Admin);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantService>();
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();

            tenant.SetWorkspace(other.Workspace.Id);
            (await db.Invitations.AnyAsync(i => i.Email == email)).Should().BeFalse();

            tenant.SetWorkspace(owner.Workspace.Id);
            var visible = await db.Invitations.SingleAsync(i => i.Email == email);
            visible.WorkspaceId.Should().Be(owner.Workspace.Id);
            visible.TokenHash.Should().Be(InviteTokens.Hash(token));
            visible.TokenHash.Should().NotContain(token);
        }

        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email);
        var accept = await AcceptAsync(invitee, token);
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertMemberAsync(invitee, owner.Workspace.Id, WorkspaceRole.Admin);
        await AssertMemberAsync(invitee, other.Workspace.Id, null);
    }

    [Fact]
    public async Task CreateInvite_LogsNeverContainTheToken()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddSingleton<ILoggerFactory>(_ =>
                    LoggerFactory.Create(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(logs);
                        // Same floor as appsettings: Information. MVC's Trace action log prints
                        // every argument, including this token and register passwords, and the
                        // app keeps Microsoft.AspNetCore at Warning so that line is not emitted.
                        logging.SetMinimumLevel(LogLevel.Information);
                    }));
            });
        });

        var owner = await TestAuthHelper.RegisterAsync(factory.CreateClient());
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var (token, link) = await InvitePendingAsync(owner, email, WorkspaceRole.Member);
        var invitee = await TestAuthHelper.RegisterAsync(factory.CreateClient(), email);
        (await AcceptAsync(invitee, token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AcceptAsync(invitee, NewToken())).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var written = string.Join('\n', logs.Messages);
        written.Should().Contain("Invitation created");
        written.Should().NotContain(token);
        written.Should().NotContain(link);
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static List<string> PropertyNames(JsonDocument document) =>
        document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

    private static async Task<HttpResponseMessage> AcceptAsync(RegisteredUser user, string token) =>
        await user.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));

    private static async Task<(string Token, string Link)> InvitePendingAsync(
        RegisteredUser owner,
        string email,
        WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, role));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var invitation = JsonSerializer.Deserialize<InvitationDto>(body, TestAuthHelper.JsonOptions);
        var link = invitation!.InviteLink ?? throw new InvalidOperationException("Pending invite did not return a link.");
        return (TestAuthHelper.TokenFromInviteLink(link), link);
    }

    private async Task AssertRawTokenIsAbsentAsync(string email, string token)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var stored = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Email == email);
        stored.TokenHash.Should().Be(InviteTokens.Hash(token));
        stored.TokenHash.Should().NotBe(token);

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT row_to_json(i)::text
            FROM "Invitations" i
            WHERE "Email" = @email
            """;
        var emailParameter = command.CreateParameter();
        emailParameter.ParameterName = "email";
        emailParameter.Value = email;
        command.Parameters.Add(emailParameter);
        var row = (string?)await command.ExecuteScalarAsync();
        row.Should().NotBeNull();
        row!.Should().NotContain(token);

        command.Parameters.Clear();
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

    private async Task AssertMemberAsync(RegisteredUser user, Guid workspaceId, WorkspaceRole? role)
    {
        var response = await user.Client.GetAsync("/api/workspaces");
        response.EnsureSuccessStatusCode();
        var workspaces = await response.Content.ReadFromJsonAsync<List<WorkspaceDto>>(TestAuthHelper.JsonOptions);
        var match = workspaces!.SingleOrDefault(workspace => workspace.Id == workspaceId);
        if (role is null)
            match.Should().BeNull();
        else
            match!.MyRole.Should().Be(role);
    }
}
