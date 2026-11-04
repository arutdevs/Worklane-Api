using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Fixes;

public class E2EBackendFixTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public E2EBackendFixTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_ConcurrentSameEmail_OneCreatedAndOneConflict_WithNoOrphanRows()
    {
        var email = $"race-email-{Guid.NewGuid():N}@example.com";
        var display = "Race User";
        var body = new RegisterRequest(email, "Password123!", display);
        using var clientA = _factory.CreateClient();
        using var clientB = _factory.CreateClient();

        var pending = await Task.WhenAll(
            clientA.PostAsJsonAsync("/api/auth/register", body),
            clientB.PostAsJsonAsync("/api/auth/register", body));

        var statuses = pending.Select(response => response.StatusCode).OrderBy(status => status).ToArray();
        statuses.Should().Equal(HttpStatusCode.Created, HttpStatusCode.Conflict);
        pending.Should().OnlyContain(response => response.StatusCode != HttpStatusCode.InternalServerError);

        var conflict = pending.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().Should().Be("email_already_registered");
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(409);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var users = await db.Users.Where(user => user.NormalizedEmail == email.ToUpperInvariant()).ToListAsync();
        users.Should().HaveCount(1);
        var userId = users[0].Id;
        users[0].PreferredLanguage.Should().Be("en");

        var memberships = await db.WorkspaceMembers.IgnoreQueryFilters().Where(member => member.UserId == userId).ToListAsync();
        memberships.Should().ContainSingle();
        memberships[0].Role.Should().Be(WorkspaceRole.Owner);

        (await db.RefreshTokens.CountAsync(token => token.UserId == userId)).Should().Be(1);
        (await db.Workspaces.CountAsync(workspace => workspace.Id == memberships[0].WorkspaceId)).Should().Be(1);
        (await db.Workspaces.CountAsync(workspace => workspace.Name == $"{display}'s Workspace" && workspace.Id != memberships[0].WorkspaceId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Register_ThaiPreferredLanguage_SeedsThaiWorkspaceAndColumns()
    {
        var email = $"th-field-{Guid.NewGuid():N}@example.com";
        var user = await RegisterAsync(email, "สมชาย", preferredLanguage: "th-TH", acceptLanguage: "en");
        user.Tokens.User.PreferredLanguage.Should().Be("th");
        user.Workspace.Name.Should().Be("เวิร์กสเปซของ สมชาย");

        var columns = await CreateProjectColumnsAsync(user, "TH");
        columns.Select(column => column.Name).Should().Equal("ต้องทำ", "กำลังทำ", "เสร็จแล้ว");
        columns.Single(column => column.IsInProgress).Name.Should().Be("กำลังทำ");
        columns.Single(column => column.IsDone).Name.Should().Be("เสร็จแล้ว");
        columns.Should().OnlyContain(column => column.Name.Contains('\u0E33') || column.Name == "เสร็จแล้ว");
    }

    [Fact]
    public async Task Register_AcceptLanguageThai_SeedsThaiWhenTheBodyOmitsLocale()
    {
        var email = $"th-header-{Guid.NewGuid():N}@example.com";
        var user = await RegisterAsync(email, "ทีมงานน้ำ", acceptLanguage: "th-TH,th;q=0.9,en;q=0.8");
        user.Tokens.User.PreferredLanguage.Should().Be("th");
        user.Workspace.Name.Should().Be("เวิร์กสเปซของ ทีมงานน้ำ");
        user.Workspace.Slug.Should().Be("ทีมงานน้ำ-workspace");
        user.Workspace.Slug.Should().Contain("\u0E33");
        user.Workspace.Slug.Should().NotContain("\u0E4D");
    }

    [Fact]
    public async Task Register_EnglishBody_IgnoresThaiAcceptLanguage()
    {
        var email = $"en-body-{Guid.NewGuid():N}@example.com";
        var user = await RegisterAsync(email, "Alice", preferredLanguage: "en-US", acceptLanguage: "th");
        user.Tokens.User.PreferredLanguage.Should().Be("en");
        user.Workspace.Name.Should().Be("Alice's Workspace");
        var columns = await CreateProjectColumnsAsync(user, "EN");
        columns.Select(column => column.Name).Should().Equal("To Do", "In Progress", "Done");
    }

    [Fact]
    public async Task CreateProject_DuplicateKey_Returns409_AndADatabaseCollisionDoesToo()
    {
        var user = await RegisterAsync($"keys-{Guid.NewGuid():N}@example.com", "Keys");
        var first = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new CreateProjectRequest("One", "WL", null));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new CreateProjectRequest("Two", "WL", null));
        await AssertConflict(second, "project_key_taken", "A project with this key already exists in this workspace.");

        await WithProjectsInsertTrigger(async () =>
        {
            var raced = await user.Client.PostAsJsonAsync(
                $"/api/workspaces/{user.Workspace.Id}/projects",
                new CreateProjectRequest("Three", "ZX", null));
            await AssertConflict(raced, "project_key_taken", "A project with this key already exists in this workspace.");
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        (await db.Projects.IgnoreQueryFilters().CountAsync(project => project.WorkspaceId == user.Workspace.Id && project.Key == "ZX"))
            .Should().Be(0);
        (await db.Projects.IgnoreQueryFilters().CountAsync(project => project.WorkspaceId == user.Workspace.Id && project.Key == "WL"))
            .Should().Be(1);
    }

    [Fact]
    public async Task CreateProject_ConcurrentSameKey_DoesNotReturn500()
    {
        var user = await RegisterAsync($"key-race-{Guid.NewGuid():N}@example.com", "Key Race");
        var request = new CreateProjectRequest("Race", "RR", null);
        var pending = await Task.WhenAll(
            user.Client.PostAsJsonAsync($"/api/workspaces/{user.Workspace.Id}/projects", request),
            user.Client.PostAsJsonAsync($"/api/workspaces/{user.Workspace.Id}/projects", request));

        pending.Should().OnlyContain(response =>
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);
        pending.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        var conflict = pending.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertConflict(conflict, "project_key_taken", "A project with this key already exists in this workspace.");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        (await db.Projects.IgnoreQueryFilters().CountAsync(project => project.WorkspaceId == user.Workspace.Id && project.Key == "RR"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Invite_ReturnsConfiguredLink_AndDuplicateMemberIs409()
    {
        var owner = await RegisterAsync($"owner-invite-{Guid.NewGuid():N}@example.com", "Owner");
        var member = await RegisterAsync($"member-invite-{Guid.NewGuid():N}@example.com", "Member");

        var added = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(member.Tokens.User.Email, WorkspaceRole.Member));
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var addedBody = await added.Content.ReadFromJsonAsync<InvitationDto>(TestAuthHelper.JsonOptions);
        addedBody!.InviteLink.Should().BeNull();

        var again = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(member.Tokens.User.Email, WorkspaceRole.Member));
        await AssertConflict(again, "workspace_member_exists", "This user is already a member of the workspace.");

        var pendingEmail = $"pending-{Guid.NewGuid():N}@example.com";
        var pending = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(pendingEmail, WorkspaceRole.Member));
        var pendingText = await pending.Content.ReadAsStringAsync();
        pending.StatusCode.Should().Be(HttpStatusCode.OK, pendingText);
        var invitation = JsonSerializer.Deserialize<InvitationDto>(pendingText, TestAuthHelper.JsonOptions);
        invitation!.InviteLink.Should().StartWith("http://web.test/invitations/accept?token=");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var stored = await db.Invitations.IgnoreQueryFilters().SingleAsync(row => row.Email == pendingEmail);
        const string marker = "token=";
        var tokenStart = invitation.InviteLink!.IndexOf(marker, StringComparison.Ordinal);
        tokenStart.Should().BeGreaterThanOrEqualTo(0);
        var token = invitation.InviteLink[(tokenStart + marker.Length)..];
        invitation.InviteLink.Should().Be(InviteLinks.ForToken("http://web.test", token));
        stored.TokenHash.Should().Be(InviteTokens.Hash(token));
        stored.TokenHash.Should().NotBe(token);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \n ")]
    public async Task CreateAndUpdateTask_RejectWhitespaceTitle(string title)
    {
        var board = await BoardTestSetup.CreateAsync(_factory);
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title,
            priority = "Medium",
            columnId = board.Todo.Id
        });
        await AssertValidation(created, "Title", "not_empty");

        var task = await BoardTestSetup.CreateTaskAsync(board, "Keep me");
        using var body = new StringContent($"{{\"title\":{JsonSerializer.Serialize(title)}}}", Encoding.UTF8, "application/json");
        var updated = await board.Client.PatchAsync($"/api/tasks/{task.Id}", body);
        await AssertValidation(updated, "Title", "not_empty");

        var reloaded = await board.Client.GetAsync($"/api/tasks/{task.Id}");
        reloaded.EnsureSuccessStatusCode();
        var dto = await reloaded.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        dto!.Title.Should().Be("Keep me");
    }

    [Fact]
    public async Task CreateWorkspace_RequiresAuth_MakesTheCallerOwner_AndKeepsTheSlugUnique()
    {
        var anonymous = _factory.CreateClient();
        var denied = await anonymous.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("Secret"));
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = await RegisterAsync($"ws-{Guid.NewGuid():N}@example.com", "Owner");
        var outsider = await RegisterAsync($"ws-out-{Guid.NewGuid():N}@example.com", "Outsider");

        var blank = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("   "));
        await AssertValidation(blank, "Name", "not_empty");

        const string name = "สำนักงาน";
        var created = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name));
        var createdText = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK, createdText);
        var workspace = JsonSerializer.Deserialize<WorkspaceDto>(createdText, TestAuthHelper.JsonOptions);
        workspace!.Name.Should().Be(name);
        workspace.Slug.Should().Be(name);
        workspace.Slug.Should().Contain("\u0E33");
        workspace.MyRole.Should().Be(WorkspaceRole.Owner);

        var second = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name));
        second.EnsureSuccessStatusCode();
        var again = await second.Content.ReadFromJsonAsync<WorkspaceDto>(TestAuthHelper.JsonOptions);
        again!.Slug.Should().Be(name + "-1");
        again.MyRole.Should().Be(WorkspaceRole.Owner);
        again.Id.Should().NotBe(workspace.Id);

        var mine = await user.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        mine!.Select(item => item.Id).Should().Contain([workspace.Id, again.Id]);

        var theirs = await outsider.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        theirs!.Select(item => item.Id).Should().NotContain([workspace.Id, again.Id]);
    }

    [Fact]
    public async Task CreateWorkspace_ConcurrentSameName_RetriesTheSlug()
    {
        var user = await RegisterAsync($"ws-race-{Guid.NewGuid():N}@example.com", "Slug");
        var name = "race" + Guid.NewGuid().ToString("N");
        var gate = _factory.Services.GetRequiredService<WorkspaceInsertGate>();
        gate.Arm(2);
        HttpResponseMessage[] pending;
        try
        {
            pending = await Task.WhenAll(
                user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name)),
                user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name)));
        }
        finally
        {
            gate.Disarm();
        }

        var bodies = new string[pending.Length];
        for (var i = 0; i < pending.Length; i++)
        {
            bodies[i] = await pending[i].Content.ReadAsStringAsync();
            pending[i].StatusCode.Should().Be(HttpStatusCode.OK, bodies[i]);
        }

        var slugs = bodies
            .Select(body => JsonDocument.Parse(body).RootElement.GetProperty("slug").GetString())
            .ToArray();
        slugs.Should().BeEquivalentTo(name, name + "-1");
        gate.Arrivals.Should().BeGreaterThanOrEqualTo(2);
    }

    private async Task<RegisteredUser> RegisterAsync(
        string email,
        string displayName,
        string? preferredLanguage = null,
        string? acceptLanguage = null)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new RegisterRequest(email, "Password123!", displayName, preferredLanguage))
        };
        if (!string.IsNullOrEmpty(acceptLanguage))
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Register did not return tokens.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var workspaces = await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Could not read workspaces.");
        var workspace = workspaces.Single(item => item.Name == WorkspaceLocale.WorkspaceName(displayName, tokens.User.PreferredLanguage));
        return new RegisteredUser(client, tokens, workspace);
    }

    private static async Task<List<BoardColumnDto>> CreateProjectColumnsAsync(RegisteredUser user, string key)
    {
        var created = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new CreateProjectRequest("Board", key, null));
        var text = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var project = JsonSerializer.Deserialize<ProjectDto>(text, TestAuthHelper.JsonOptions)!;
        var columns = await user.Client.GetFromJsonAsync<List<BoardColumnDto>>(
            $"/api/projects/{project.Id}/columns",
            TestAuthHelper.JsonOptions);
        return columns!;
    }

    private async Task WithProjectsInsertTrigger(Func<Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        const string sql = """
            CREATE OR REPLACE FUNCTION wl_project_key_conflict() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
              RAISE EXCEPTION 'duplicate key value violates unique constraint "IX_Projects_WorkspaceId_Key"'
                USING ERRCODE = 'unique_violation',
                      CONSTRAINT = 'IX_Projects_WorkspaceId_Key',
                      TABLE = 'Projects',
                      SCHEMA = 'public';
            END;
            $fn$;
            DROP TRIGGER IF EXISTS wl_project_key_conflict ON "Projects";
            CREATE TRIGGER wl_project_key_conflict
            BEFORE INSERT ON "Projects"
            FOR EACH ROW EXECUTE FUNCTION wl_project_key_conflict();
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
        try
        {
            await action();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS wl_project_key_conflict ON "Projects";
                DROP FUNCTION IF EXISTS wl_project_key_conflict();
                """);
        }
    }

    private static async Task AssertConflict(HttpResponseMessage response, string code, string detail)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be(code);
        doc.RootElement.GetProperty("detail").GetString().Should().Be(detail);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Conflict");
        text.Should().NotContain("23505");
        text.Should().NotContain("IX_");
    }

    private static async Task AssertValidation(HttpResponseMessage response, string field, string fieldCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        doc.RootElement.GetProperty("fieldCodes").GetProperty(field).EnumerateArray().Select(item => item.GetString())
            .Should().Contain(fieldCode);
    }

}
