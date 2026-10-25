using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Worklane.Application.Auth.Dtos;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Auth;

public class AuthFlowTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public AuthFlowTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_CreatesUserAndPersonalWorkspace()
    {
        var client = _factory.CreateClient();
        var email = $"register-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
        tokens.Should().NotBeNull();
        tokens!.User.Email.Should().Be(email);
        tokens.AccessToken.Should().NotBeNullOrEmpty();
        tokens.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Alice"));

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Alice Again"));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().Should().Be("email_already_registered");
        doc.RootElement.GetProperty("detail").GetString().Should().Be("An account with this email already exists.");
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        var client = _factory.CreateClient();
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Bob"));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Password123!"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>();
        tokens!.User.Email.Should().Be(email);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var email = $"badpw-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Carl"));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndInvalidatesThePrevious()
    {
        var client = _factory.CreateClient();
        var email = $"refresh-{Guid.NewGuid():N}@example.com";
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Dana"));
        var originalTokens = await registerResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();

        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(originalTokens!.RefreshToken));
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var newTokens = await refreshResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();
        newTokens!.RefreshToken.Should().NotBe(originalTokens.RefreshToken);

        var reuseResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(originalTokens.RefreshToken));
        reuseResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var client = _factory.CreateClient();
        var email = $"logout-{Guid.NewGuid():N}@example.com";
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Erin"));
        var tokens = await registerResponse.Content.ReadFromJsonAsync<AuthTokensResponse>();

        var logoutResponse = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens!.RefreshToken));
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshAfterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        refreshAfterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_ThaiName_KeepsVowelsAndToneMarks_AndStaysUnique()
    {
        var first = await RegisterSlug("พี่น้อง");
        var second = await RegisterSlug("พี่น้อง");
        first.Should().Be("พี่น้อง-workspace");
        second.Should().Be("พี่น้อง-workspace-1");
    }

    [Fact]
    public async Task Register_MixedThaiAndEnglish_KeepsMarks()
    {
        (await RegisterSlug("ทีม Alpha")).Should().Be("ทีม-alpha-workspace");
    }

    [Fact]
    public async Task Register_MarksOrSymbols_FallBackToASafeSlug_AndStayUnique()
    {
        (await RegisterSlug("@@@")).Should().Be(WorkspaceSlug.Fallback);
        (await RegisterSlug("ิ่้๊๋")).Should().Be(WorkspaceSlug.Fallback + "-1");
    }

    [Fact]
    public async Task Register_FullwidthDisplayName_NormalizesSlug()
    {
        (await RegisterSlug("Ｗｏｒｋｌａｎｅ")).Should().Be("worklane-workspace");
    }

    [Fact]
    public async Task Register_TrimsDisplayName_BeforeSlugging()
    {
        var core = "trim" + Guid.NewGuid().ToString("N");
        (await RegisterSlug($"  {core}  ")).Should().Be(core + "-workspace");
    }

    [Fact]
    public async Task CreateWorkspace_FullwidthName_SlugIsWorklane()
    {
        var (client, _) = await RegisterAndAuthorize("fullwidth");
        var created = await client.PostAsJsonAsync("/api/workspaces", new { name = "Ｗｏｒｋｌａｎｅ" });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("slug").GetString().Should().Be("worklane");
    }

    [Fact]
    public async Task CreateWorkspace_TrimsName_BeforeSlugging()
    {
        var (client, _) = await RegisterAndAuthorize("trim-ws");
        var core = "trimws" + Guid.NewGuid().ToString("N");
        var created = await client.PostAsJsonAsync("/api/workspaces", new { name = $"  {core}  " });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("slug").GetString().Should().Be(core);
    }

    [Fact]
    public async Task CreateWorkspace_ThaiName_KeepsVowelsAndToneMarks()
    {
        var client = _factory.CreateClient();
        var email = $"slug-{Guid.NewGuid():N}@example.com";
        var registered = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Erin"));
        var tokens = await registered.Content.ReadFromJsonAsync<AuthTokensResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        const string name = "ห้องไทย";
        var created = await client.PostAsJsonAsync("/api/workspaces", new { name });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("slug").GetString().Should().Be(name);
    }

    private async Task<(HttpClient Client, AuthTokensResponse Tokens)> RegisterAndAuthorize(string prefix)
    {
        var client = _factory.CreateClient();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", prefix));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(body, JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, tokens);
    }

    private async Task<string> RegisterSlug(string displayName)
    {
        var client = _factory.CreateClient();
        var email = $"slug-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "Password123!", displayName));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var tokens = JsonSerializer.Deserialize<AuthTokensResponse>(body, JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var list = await client.GetAsync("/api/workspaces");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var expectedName = $"{displayName}'s Workspace";
        foreach (var workspace in doc.RootElement.EnumerateArray())
        {
            if (workspace.GetProperty("name").GetString() == expectedName)
                return workspace.GetProperty("slug").GetString()!;
        }

        throw new InvalidOperationException($"No workspace named {expectedName}.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
