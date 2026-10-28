using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Common.Options;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Auth;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Regression;

/// <summary>
/// API-level regression for the E2E1 flows QA signed off on develop.
/// Test-only: these cases drive the public HTTP API and do not change production code.
/// </summary>
public class E2E1RegressionTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private const string Password = "Password123!";

    private readonly WorklaneWebApplicationFactory _factory;

    public E2E1RegressionTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_CreatesDefaultWorkspace_ThaiSlugKeepsMarks_DuplicateGetsSuffix_ThaiLocaleSeedsColumns()
    {
        const string displayName = "บริษัทข้าวหอมจำกัด";
        displayName.Should().Contain("\u0E33");
        // NFKC would split SARA AM (U+0E33) into NIKHAHIT (U+0E4D) + SARA AA. The slug must not.
        "\u0E33".Normalize(System.Text.NormalizationForm.FormKC).Should().Be("\u0E4D\u0E32");

        var first = await RegisterAsync(
            $"e2e-th-{Guid.NewGuid():N}@example.com",
            displayName,
            preferredLanguage: "th");

        first.Tokens.User.PreferredLanguage.Should().Be("th");
        first.Tokens.User.DisplayName.Should().Be(displayName);
        first.Workspace.MyRole.Should().Be(WorkspaceRole.Owner);
        first.Workspace.Name.Should().Be($"เวิร์กสเปซของ {displayName}");
        first.Workspace.Slug.Should().Be($"{displayName}-workspace");
        AssertThaiMarksPreserved(first.Workspace.Slug);

        var project = await CreateProjectAsync(first, "Thai Board", "TH");
        var columns = await GetColumnsAsync(first.Client, project.Id);
        columns.Select(column => column.Name).Should().Equal("ต้องทำ", "กำลังทำ", "เสร็จแล้ว");
        columns.Single(column => column.IsInProgress).Name.Should().Be("กำลังทำ");
        columns.Single(column => column.IsDone).Name.Should().Be("เสร็จแล้ว");

        var second = await RegisterAsync(
            $"e2e-th-dup-{Guid.NewGuid():N}@example.com",
            displayName);

        second.Workspace.Id.Should().NotBe(first.Workspace.Id);
        second.Workspace.MyRole.Should().Be(WorkspaceRole.Owner);
        second.Workspace.Slug.Should().Be($"{displayName}-workspace-1");
        AssertThaiMarksPreserved(second.Workspace.Slug);

        var firstAgain = await first.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        firstAgain.Should().ContainSingle().Which.Slug.Should().Be($"{displayName}-workspace");
    }

    [Fact]
    public async Task Register_ConcurrentDuplicateEmail_OneSuccessAndTheOtherConflict()
    {
        var email = $"e2e-race-{Guid.NewGuid():N}@example.com";
        var body = new RegisterRequest(email, Password, "Race User");
        using var left = _factory.CreateClient();
        using var right = _factory.CreateClient();

        var pending = await Task.WhenAll(
            left.PostAsJsonAsync("/api/auth/register", body),
            right.PostAsJsonAsync("/api/auth/register", body));

        pending.Should().OnlyContain(response => response.StatusCode != HttpStatusCode.InternalServerError);
        pending.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var conflict = pending.Single(response => response.StatusCode != HttpStatusCode.Created);
        await AssertProblem(conflict, HttpStatusCode.Conflict, "email_already_registered");
    }

    [Fact]
    public async Task CreateProject_ConcurrentDuplicateKey_OneSuccessAndTheOthersConflict()
    {
        var user = await RegisterAsync($"e2e-key-{Guid.NewGuid():N}@example.com", "Key Race");
        var request = new CreateProjectRequest("Race", "E2E", null);
        var url = $"/api/workspaces/{user.Workspace.Id}/projects";

        using var extraA = AuthorizedClient(user);
        using var extraB = AuthorizedClient(user);
        using var extraC = AuthorizedClient(user);
        var clients = new[] { user.Client, extraA, extraB, extraC };

        var pending = await Task.WhenAll(clients.Select(client => client.PostAsJsonAsync(url, request)));

        pending.Should().OnlyContain(response => response.StatusCode != HttpStatusCode.InternalServerError);
        var successes = pending.Where(response => response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created).ToArray();
        successes.Should().ContainSingle();
        var conflicts = pending.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToArray();
        conflicts.Should().HaveCount(pending.Length - 1);
        foreach (var conflict in conflicts)
            await AssertProblem(conflict, HttpStatusCode.Conflict, "project_key_taken");

        var project = await successes[0].Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions);
        project!.Key.Should().Be("E2E");
        project.WorkspaceId.Should().Be(user.Workspace.Id);

        var listed = await user.Client.GetFromJsonAsync<List<ProjectDto>>(url, TestAuthHelper.JsonOptions);
        listed.Should().ContainSingle().Which.Key.Should().Be("E2E");
    }

    [Fact]
    public async Task CreateWorkspace_EmptyName_IsValidationError()
    {
        var user = await RegisterAsync($"e2e-ws-{Guid.NewGuid():N}@example.com", "Workspace User");

        foreach (var name in new[] { "", "   " })
        {
            var response = await user.Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name));
            await AssertValidation(response, "Name", "not_empty");
        }

        var workspaces = await user.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions);
        workspaces.Should().ContainSingle().Which.Id.Should().Be(user.Workspace.Id);
    }

    [Fact]
    public async Task Task_CreateWithAssigneeAndDueDate_RejectsEmptyTitle_CommentAndMovePersist()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"e2e-card-{Guid.NewGuid():N}@example.com", "Niran");
        var due = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

        var empty = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id,
            dueDate = due,
            columnId = board.Todo.Id
        });
        await AssertValidation(empty, "Title", "not_empty");

        var before = await board.Client.GetFromJsonAsync<List<TaskItemDto>>(
            $"/api/projects/{board.Project.Id}/tasks",
            TestAuthHelper.JsonOptions);
        before.Should().BeEmpty();

        var createdResponse = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Ship signup",
            description = "QA regression card",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id,
            dueDate = due,
            columnId = board.Todo.Id
        });
        var createdText = await createdResponse.Content.ReadAsStringAsync();
        createdResponse.StatusCode.Should().Be(HttpStatusCode.Created, createdText);
        var created = JsonSerializer.Deserialize<TaskItemDto>(createdText, TestAuthHelper.JsonOptions);
        created!.Title.Should().Be("Ship signup");
        created.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        created.AssigneeName.Should().Be("Niran");
        created.DueDate.Should().Be(due);
        created.ColumnId.Should().Be(board.Todo.Id);

        var read = await board.Client.GetFromJsonAsync<TaskItemDto>($"/api/tasks/{created.Id}", TestAuthHelper.JsonOptions);
        read!.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        read.AssigneeName.Should().Be("Niran");
        read.DueDate.Should().Be(due);
        read.ColumnId.Should().Be(board.Todo.Id);

        var commentResponse = await board.Client.PostAsJsonAsync($"/api/tasks/{created.Id}/comments", new { body = "Signed off" });
        var commentText = await commentResponse.Content.ReadAsStringAsync();
        commentResponse.StatusCode.Should().Be(HttpStatusCode.Created, commentText);
        var comment = JsonSerializer.Deserialize<CommentDto>(commentText, TestAuthHelper.JsonOptions);
        comment!.Body.Should().Be("Signed off");
        comment.TaskId.Should().Be(created.Id);

        var comments = await board.Client.GetFromJsonAsync<List<CommentDto>>(
            $"/api/tasks/{created.Id}/comments",
            TestAuthHelper.JsonOptions);
        comments.Should().ContainSingle().Which.Body.Should().Be("Signed off");

        var moveResponse = await board.Client.PostAsJsonAsync($"/api/tasks/{created.Id}/move", new
        {
            toColumnId = board.InProgress.Id,
            toIndex = 0
        });
        var moveText = await moveResponse.Content.ReadAsStringAsync();
        moveResponse.StatusCode.Should().Be(HttpStatusCode.OK, moveText);

        var moved = await board.Client.GetFromJsonAsync<TaskItemDto>($"/api/tasks/{created.Id}", TestAuthHelper.JsonOptions);
        moved!.ColumnId.Should().Be(board.InProgress.Id);
        moved.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        moved.DueDate.Should().Be(due);

        var columns = await GetColumnsAsync(board.Client, board.Project.Id);
        columns.Single(column => column.Id == board.Todo.Id).Tasks.Should().NotContain(task => task.Id == created.Id);
        var inProgress = columns.Single(column => column.Id == board.InProgress.Id);
        var card = inProgress.Tasks.Should().ContainSingle(task => task.Id == created.Id).Subject;
        card.AssigneeId.Should().Be(board.User.Tokens.User.Id);
        card.DueDate.Should().Be(due);

        var commentsAfterMove = await board.Client.GetFromJsonAsync<List<CommentDto>>(
            $"/api/tasks/{created.Id}/comments",
            TestAuthHelper.JsonOptions);
        commentsAfterMove.Should().ContainSingle().Which.Body.Should().Be("Signed off");
    }

    [Fact]
    public async Task Login_UppercaseEmailSucceeds_WrongPasswordIsUnauthorized()
    {
        var email = $"Ada.{Guid.NewGuid():N}@Example.com";
        await RegisterAsync(email, "Ada");

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email.ToUpperInvariant(), Password));
        var loginText = await login.Content.ReadAsStringAsync();
        login.StatusCode.Should().Be(HttpStatusCode.OK, loginText);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(loginText, TestAuthHelper.JsonOptions);
        tokens!.User.Email.Should().Be(email);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email.ToUpperInvariant(), "WrongPassword1"));
        await AssertProblem(wrong, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Invite_OwnerLinkUsesFrontendOrigin_InviteeJoinsAndReadsBoard_ReuseIsNotFound_MemberIsForbidden()
    {
        var owner = await RegisterAsync($"e2e-owner-{Guid.NewGuid():N}@example.com", "Owner");
        var project = await CreateProjectAsync(owner, "Invite Board", "INV");
        var pendingEmail = $"e2e-invitee-{Guid.NewGuid():N}@example.com";

        var invite = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(pendingEmail, WorkspaceRole.Member));
        var inviteText = await invite.Content.ReadAsStringAsync();
        invite.StatusCode.Should().Be(HttpStatusCode.OK, inviteText);

        using var inviteJson = JsonDocument.Parse(inviteText);
        inviteJson.RootElement.TryGetProperty("inviteLink", out var linkProperty).Should().BeTrue();
        linkProperty.ValueKind.Should().Be(JsonValueKind.String);
        var inviteLink = linkProperty.GetString();
        inviteLink.Should().NotBeNullOrWhiteSpace();

        var frontend = _factory.Services.GetRequiredService<IOptions<FrontendSettings>>().Value.BaseUrl.Trim().TrimEnd('/');
        var link = new Uri(inviteLink!, UriKind.Absolute);
        link.GetLeftPart(UriPartial.Authority).Should().Be(frontend);
        link.AbsolutePath.Should().Be("/invitations/accept");
        var token = QueryHelpers.ParseQuery(link.Query)["token"].ToString();
        token.Should().NotBeNullOrWhiteSpace();

        var invitee = await RegisterAsync(pendingEmail, "Invitee");
        var accept = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var members = await invitee.Client.GetFromJsonAsync<List<WorkspaceMemberDto>>(
            $"/api/workspaces/{owner.Workspace.Id}/members",
            TestAuthHelper.JsonOptions);
        members.Should().Contain(member => member.UserId == invitee.Tokens.User.Id && member.Role == WorkspaceRole.Member);

        var projects = await invitee.Client.GetFromJsonAsync<List<ProjectDto>>(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            TestAuthHelper.JsonOptions);
        projects.Should().ContainSingle().Which.Id.Should().Be(project.Id);
        var columns = await GetColumnsAsync(invitee.Client, project.Id);
        columns.Should().NotBeEmpty();

        var reuse = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        await AssertProblem(reuse, HttpStatusCode.NotFound, "not_found");

        var stranger = await RegisterAsync($"e2e-stranger-{Guid.NewGuid():N}@example.com", "Stranger");
        var strangerReuse = await stranger.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        await AssertProblem(strangerReuse, HttpStatusCode.NotFound, "not_found");

        var unknown = await stranger.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(Convert.ToHexString(Guid.NewGuid().ToByteArray())));
        await AssertProblem(unknown, HttpStatusCode.NotFound, "not_found");

        var memberOnly = await RegisterAsync($"e2e-member-{Guid.NewGuid():N}@example.com", "Member");
        var addMember = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(memberOnly.Tokens.User.Email, WorkspaceRole.Member));
        var addText = await addMember.Content.ReadAsStringAsync();
        addMember.StatusCode.Should().Be(HttpStatusCode.OK, addText);
        using var addJson = JsonDocument.Parse(addText);
        var memberToken = QueryHelpers.ParseQuery(new Uri(addJson.RootElement.GetProperty("inviteLink").GetString()!, UriKind.Absolute).Query)["token"].ToString();
        var memberAccept = await memberOnly.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(memberToken));
        memberAccept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberInvite = await memberOnly.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest($"e2e-denied-{Guid.NewGuid():N}@example.com", WorkspaceRole.Member));
        await AssertProblem(memberInvite, HttpStatusCode.Forbidden, "workspace_access_denied");
    }

    [Fact]
    public async Task Invite_AnotherUsersLiveToken_IsNotFound()
    {
        var owner = await RegisterAsync($"e2e-live-owner-{Guid.NewGuid():N}@example.com", "Owner");
        var pendingEmail = $"e2e-live-invitee-{Guid.NewGuid():N}@example.com";
        var token = await InvitePendingTokenAsync(owner, pendingEmail);

        var other = await RegisterAsync($"e2e-live-other-{Guid.NewGuid():N}@example.com", "Other");
        var response = await other.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        await AssertProblem(response, HttpStatusCode.NotFound, "not_found");

        var invitee = await RegisterAsync(pendingEmail, "Invitee");
        var accept = await invitee.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Tenancy_OutsiderMatchesRandomId_OwnerCanRead()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"e2e-tenant-{Guid.NewGuid():N}@example.com", "Owner");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Hidden card",
            priority = "Low",
            columnId = board.Todo.Id
        });
        created.EnsureSuccessStatusCode();
        var task = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        var comment = await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Secret" });
        comment.EnsureSuccessStatusCode();

        var outsider = await RegisterAsync($"e2e-outsider-{Guid.NewGuid():N}@example.com", "Outsider");
        var workspaceId = board.User.Workspace.Id;
        var missingWorkspace = Guid.NewGuid();
        var missingProject = Guid.NewGuid();
        var missingTask = Guid.NewGuid();

        await AssertHiddenMatchesMissing(
            outsider.Client,
            board.Client,
            $"/api/workspaces/{workspaceId}/projects",
            $"/api/workspaces/{missingWorkspace}/projects",
            workspaceId,
            missingWorkspace);
        await AssertHiddenMatchesMissing(
            outsider.Client,
            board.Client,
            $"/api/projects/{board.Project.Id}/columns",
            $"/api/projects/{missingProject}/columns",
            board.Project.Id,
            missingProject);
        await AssertHiddenMatchesMissing(
            outsider.Client,
            board.Client,
            $"/api/tasks/{task.Id}",
            $"/api/tasks/{missingTask}",
            task.Id,
            missingTask);
        await AssertHiddenMatchesMissing(
            outsider.Client,
            board.Client,
            $"/api/tasks/{task.Id}/comments",
            $"/api/tasks/{missingTask}/comments",
            task.Id,
            missingTask);
        await AssertHiddenMatchesMissing(
            outsider.Client,
            board.Client,
            $"/api/workspaces/{workspaceId}/members",
            $"/api/workspaces/{missingWorkspace}/members",
            workspaceId,
            missingWorkspace);

        (await board.Client.GetAsync($"/api/workspaces/{workspaceId}/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await board.Client.GetAsync($"/api/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await board.Client.GetAsync($"/api/tasks/{task.Id}/comments")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await board.Client.GetAsync($"/api/workspaces/{workspaceId}/members")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<RegisteredUser> RegisterAsync(
        string email,
        string displayName,
        string? preferredLanguage = null)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, displayName, preferredLanguage));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Register did not return tokens.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var workspaces = await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Could not read workspaces.");
        workspaces.Should().ContainSingle();
        workspaces[0].MyRole.Should().Be(WorkspaceRole.Owner);
        return new RegisteredUser(client, tokens, workspaces[0]);
    }

    private HttpClient AuthorizedClient(RegisteredUser user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Tokens.AccessToken);
        return client;
    }

    private static async Task<ProjectDto> CreateProjectAsync(RegisteredUser user, string name, string key)
    {
        var response = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new CreateProjectRequest(name, key, null));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<ProjectDto>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Project was not created.");
    }

    private static async Task<List<BoardColumnDto>> GetColumnsAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/projects/{projectId}/columns");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Columns were not returned.");
    }

    private static async Task<string> InvitePendingTokenAsync(RegisteredUser owner, string email)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, WorkspaceRole.Member));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        var link = new Uri(json.RootElement.GetProperty("inviteLink").GetString()!, UriKind.Absolute);
        return QueryHelpers.ParseQuery(link.Query)["token"].ToString();
    }

    private static void AssertThaiMarksPreserved(string slug)
    {
        slug.Should().Contain("\u0E33");
        slug.Should().Contain("\u0E34");
        slug.Should().Contain("\u0E31");
        slug.Should().Contain("\u0E49");
        slug.Should().NotContain("\u0E4D");
    }

    private static async Task AssertHiddenMatchesMissing(
        HttpClient outsider,
        HttpClient owner,
        string realUrl,
        string missingUrl,
        Guid realId,
        Guid missingId)
    {
        using var hidden = await outsider.GetAsync(realUrl);
        using var missing = await outsider.GetAsync(missingUrl);
        using var ownerMissing = await owner.GetAsync(missingUrl);
        using var ownerReal = await owner.GetAsync(realUrl);

        var hiddenBody = await hidden.Content.ReadAsStringAsync();
        var missingBody = await missing.Content.ReadAsStringAsync();
        var ownerMissingBody = await ownerMissing.Content.ReadAsStringAsync();

        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{realUrl} outsider: {hiddenBody}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{missingUrl} outsider: {missingBody}");
        ownerMissing.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{missingUrl} owner: {ownerMissingBody}");
        ownerReal.StatusCode.Should().Be(HttpStatusCode.OK, realUrl);

        var hiddenNormalized = NormalizeId(hiddenBody, realId);
        hiddenNormalized.Should().Be(NormalizeId(missingBody, missingId), realUrl);
        hiddenNormalized.Should().Be(NormalizeId(ownerMissingBody, missingId), realUrl);
        hiddenBody.Should().NotContain("workspace_access_denied");
    }

    private static string NormalizeId(string body, Guid id) =>
        body.Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    private static async Task AssertValidation(HttpResponseMessage response, string field, string fieldCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        document.RootElement.GetProperty("title").GetString().Should().Be("Validation Failed");
        document.RootElement.GetProperty("fieldCodes").GetProperty(field).EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain(fieldCode);
    }
}

/// <summary>
/// The shared integration host raises <c>RateLimit:Auth:PermitLimit</c> so the rest of the suite
/// can share one address. This host restores the production permit of 10, so the 11th failed
/// login is the 429 contract QA checks.
/// </summary>
public sealed class E2E1AuthRateLimitFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Auth:PermitLimit"] = "10",
                ["RateLimit:Auth:WindowSeconds"] = "60",
            });
        });
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, ClientIpFromHeaderStartupFilter>());
    }
}

public class E2E1AuthRateLimitTests : IClassFixture<E2E1AuthRateLimitFactory>
{
    private readonly E2E1AuthRateLimitFactory _factory;

    public E2E1AuthRateLimitTests(E2E1AuthRateLimitFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_EleventhFailedAttempt_IsRateLimitedWithoutNumbersInTheBody()
    {
        using var client = _factory.CreateClient();
        var email = $"e2e-limit-{Guid.NewGuid():N}@example.com";

        using var registered = await Post(
            client,
            "/api/auth/register",
            new { email, password = "Password123!", displayName = "Limited" },
            "10.71.1.1");
        registered.StatusCode.Should().Be(HttpStatusCode.Created);

        const string loginIp = "10.71.1.2";
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var failed = await Post(
                client,
                "/api/auth/login",
                new { email, password = "WrongPassword1" },
                loginIp);
            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"failed attempt {attempt}");
        }

        using var blocked = await Post(
            client,
            "/api/auth/login",
            new { email, password = "WrongPassword1" },
            loginIp);
        var body = await blocked.Content.ReadAsStringAsync();
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, body);
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        blocked.Headers.Contains("Retry-After").Should().BeTrue();
        blocked.Headers.RetryAfter.Should().NotBeNull();
        blocked.Headers.RetryAfter!.Delta.Should().NotBeNull();
        blocked.Headers.RetryAfter.Delta!.Value.Should().BeGreaterThan(TimeSpan.Zero);

        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("code").GetString().Should().Be("rate_limited");
        AssertNoNumbersExceptHttpStatus(problem.RootElement);
    }

    private static void AssertNoNumbersExceptHttpStatus(JsonElement problem)
    {
        foreach (var property in problem.EnumerateObject())
        {
            if (property.NameEquals("status"))
            {
                property.Value.GetInt32().Should().Be((int)HttpStatusCode.TooManyRequests);
                continue;
            }

            property.Value.ValueKind.Should().NotBe(JsonValueKind.Number, $"'{property.Name}' must not be numeric");
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString() ?? "";
                Regex.IsMatch(text, @"\d").Should().BeFalse($"'{property.Name}' contains a number: {text}");
            }
        }
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation(ClientIpFromHeaderStartupFilter.HeaderName, ip);
        return await client.SendAsync(request);
    }
}
