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
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Projects;

public class DefaultColumnLocaleTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private static readonly string[] EnglishNames = ["To Do", "In Progress", "Done"];
    private static readonly string[] ThaiNames = ["ต้องทำ", "กำลังทำ", "เสร็จแล้ว"];

    private readonly WorklaneWebApplicationFactory _factory;

    public DefaultColumnLocaleTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("th")]
    [InlineData("th-TH")]
    public async Task SignupEnglish_CreateWithThaiLanguage_StoresThaiColumnNames(string language)
    {
        var user = await RegisterAsync("Alice", "en");
        user.Tokens.User.PreferredLanguage.Should().Be("en");

        var project = await CreateProjectAsync(user, "TH", language, acceptLanguage: "en");
        var columns = await GetColumnsAsync(user.Client, project.Id, acceptLanguage: "en");

        columns.Select(column => column.Name).Should().Equal(ThaiNames);
        columns.Single(column => column.IsInProgress).Name.Should().Be("กำลังทำ");
        columns.Single(column => column.IsDone).Name.Should().Be("เสร็จแล้ว");
        (await StoredNamesAsync(project.Id)).Should().Equal(ThaiNames);
    }

    [Fact]
    public async Task SignupThai_CreateWithEnglishLanguage_StoresEnglishColumnNames()
    {
        var user = await RegisterAsync("สมชาย", "th");
        user.Tokens.User.PreferredLanguage.Should().Be("th");

        var project = await CreateProjectAsync(user, "EN", language: "en", acceptLanguage: "th");
        var columns = await GetColumnsAsync(user.Client, project.Id);

        columns.Select(column => column.Name).Should().Equal(EnglishNames);
        (await StoredNamesAsync(project.Id)).Should().Equal(EnglishNames);
    }

    [Fact]
    public async Task OmittedLanguage_AcceptLanguageThai_StoresThaiColumnNames()
    {
        var user = await RegisterAsync("Alice", "en");

        var project = await CreateProjectAsync(user, "HD", language: null, acceptLanguage: "th");
        (await GetColumnsAsync(user.Client, project.Id)).Select(column => column.Name).Should().Equal(ThaiNames);
        (await StoredNamesAsync(project.Id)).Should().Equal(ThaiNames);
    }

    [Theory]
    [InlineData("fr", null)]
    [InlineData("de-DE", "th")]
    [InlineData(null, "fr")]
    [InlineData(null, null)]
    public async Task UnsupportedOrMissingLocale_FallsBackToEnglish(string? language, string? acceptLanguage)
    {
        var user = await RegisterAsync("สมชาย", "th");
        user.Tokens.User.PreferredLanguage.Should().Be("th");

        var project = await CreateProjectAsync(user, "FB", language, acceptLanguage);
        (await GetColumnsAsync(user.Client, project.Id, acceptLanguage: "th")).Select(column => column.Name)
            .Should().Equal(EnglishNames);
        (await StoredNamesAsync(project.Id)).Should().Equal(EnglishNames);
    }

    [Fact]
    public async Task LanguageLongerThan35_ReturnsValidationFailed()
    {
        var user = await RegisterAsync("Alice", "en");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{user.Workspace.Id}/projects")
        {
            Content = JsonContent.Create(new
            {
                name = "Board",
                key = "LG",
                language = new string('a', 36)
            })
        };

        var response = await user.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        doc.RootElement.GetProperty("fieldCodes").GetProperty("Language").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain("maximum_length");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        (await db.Projects.IgnoreQueryFilters().CountAsync(project => project.WorkspaceId == user.Workspace.Id && project.Key == "LG"))
            .Should().Be(0);
    }

    [Fact]
    public async Task RenamedColumn_StaysRenamed()
    {
        var user = await RegisterAsync("Alice", "en");
        var project = await CreateProjectAsync(user, "RN", language: "th", acceptLanguage: null);
        var columns = await GetColumnsAsync(user.Client, project.Id);
        var todo = columns.Single(column => column.Name == "ต้องทำ");

        var renamed = await user.Client.PatchAsJsonAsync($"/api/columns/{todo.Id}", new { name = "Backlog" });
        var renamedText = await renamed.Content.ReadAsStringAsync();
        renamed.StatusCode.Should().Be(HttpStatusCode.OK, renamedText);

        var afterRename = await GetColumnsAsync(user.Client, project.Id, acceptLanguage: "en");
        afterRename.Select(column => column.Name).Should().Equal("Backlog", "กำลังทำ", "เสร็จแล้ว");

        var later = await CreateProjectAsync(user, "EN2", language: "en", acceptLanguage: "en");
        (await GetColumnsAsync(user.Client, project.Id, acceptLanguage: "th")).Select(column => column.Name)
            .Should().Equal("Backlog", "กำลังทำ", "เสร็จแล้ว");
        (await StoredNamesAsync(project.Id)).Should().Equal("Backlog", "กำลังทำ", "เสร็จแล้ว");
        (await GetColumnsAsync(user.Client, later.Id)).Select(column => column.Name).Should().Equal(EnglishNames);
    }

    [Fact]
    public async Task DefaultColumns_StayInsideTheCreatingWorkspace()
    {
        var owner = await RegisterAsync("Alice", "en");
        var outsider = await RegisterAsync("สมชาย", "th");
        outsider.Tokens.User.PreferredLanguage.Should().Be("th");

        var thaiBoard = await CreateProjectAsync(owner, "TH", language: "th", acceptLanguage: null);
        var englishBoard = await CreateProjectAsync(outsider, "EN", language: "en", acceptLanguage: "th");
        var thaiColumns = await GetColumnsAsync(owner.Client, thaiBoard.Id);
        thaiColumns.Select(column => column.Name).Should().Equal(ThaiNames);

        var hidden = await outsider.Client.GetAsync($"/api/projects/{thaiBoard.Id}/columns");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var hiddenCreate = await outsider.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/projects",
            new { name = "Nope", key = "NO", language = "en" });
        hiddenCreate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var hiddenRename = await outsider.Client.PatchAsJsonAsync(
            $"/api/columns/{thaiColumns[0].Id}",
            new { name = "Stolen" });
        hiddenRename.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var reverse = await owner.Client.GetAsync($"/api/projects/{englishBoard.Id}/columns");
        reverse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await GetColumnsAsync(owner.Client, thaiBoard.Id, acceptLanguage: "en")).Select(column => column.Name)
            .Should().Equal(ThaiNames);
        (await GetColumnsAsync(outsider.Client, englishBoard.Id, acceptLanguage: "th")).Select(column => column.Name)
            .Should().Equal(EnglishNames);
        (await StoredNamesAsync(thaiBoard.Id)).Should().Equal(ThaiNames);
        (await StoredNamesAsync(englishBoard.Id)).Should().Equal(EnglishNames);
    }

    private async Task<RegisteredUser> RegisterAsync(string displayName, string preferredLanguage)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"col-{Guid.NewGuid():N}@example.com", "Password123!", displayName, preferredLanguage));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Register did not return tokens.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var workspaces = await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Could not read workspaces.");
        return new RegisteredUser(client, tokens, workspaces.Single());
    }

    private static async Task<ProjectDto> CreateProjectAsync(
        RegisteredUser user,
        string key,
        string? language,
        string? acceptLanguage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = "Board",
            ["key"] = key,
            ["description"] = null
        };
        if (language is not null)
            payload["language"] = language;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{user.Workspace.Id}/projects")
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrEmpty(acceptLanguage))
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        var response = await user.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<ProjectDto>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Project was not created.");
    }

    private static async Task<List<BoardColumnDto>> GetColumnsAsync(HttpClient client, Guid projectId, string? acceptLanguage = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{projectId}/columns");
        if (!string.IsNullOrEmpty(acceptLanguage))
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<List<BoardColumnDto>>(text, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Columns were not returned.");
    }

    private async Task<List<string>> StoredNamesAsync(Guid projectId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.BoardColumns.IgnoreQueryFilters()
            .Where(column => column.ProjectId == projectId)
            .OrderBy(column => column.Order)
            .Select(column => column.Name)
            .ToListAsync();
    }
}
