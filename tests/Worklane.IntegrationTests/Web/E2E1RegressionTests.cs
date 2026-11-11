using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Web;

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
}
