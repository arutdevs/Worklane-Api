using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Auth;

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

        var project = await CreateProjectAsync(first, "Thai Board", "TH", "th");
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

    private static async Task<ProjectDto> CreateProjectAsync(RegisteredUser user, string name, string key, string? language = null)
    {
        var response = await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{user.Workspace.Id}/projects",
            new CreateProjectRequest(name, key, null, language));
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

    private static void AssertThaiMarksPreserved(string slug)
    {
        slug.Should().Contain("\u0E33");
        slug.Should().Contain("\u0E34");
        slug.Should().Contain("\u0E31");
        slug.Should().Contain("\u0E49");
        slug.Should().NotContain("\u0E4D");
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
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
