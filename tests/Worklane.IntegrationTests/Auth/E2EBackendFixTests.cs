using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

namespace Worklane.IntegrationTests.Auth;

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
    public async Task Register_ThaiPreferredLanguage_SeedsThaiWorkspaceName()
    {
        var email = $"th-field-{Guid.NewGuid():N}@example.com";
        var user = await RegisterAsync(email, "สมชาย", preferredLanguage: "th-TH", acceptLanguage: "en");
        user.Tokens.User.PreferredLanguage.Should().Be("th");
        user.Workspace.Name.Should().Be("เวิร์กสเปซของ สมชาย");
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
}
