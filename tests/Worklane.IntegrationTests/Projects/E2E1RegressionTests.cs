using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Projects;

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

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
    }
}
