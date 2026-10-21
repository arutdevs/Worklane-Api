using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Web;

public class NonMemberNotFoundTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public NonMemberNotFoundTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NonMember_BodyMatchesMissingId_OnEachRoute()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "hide-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Hidden card");
        var comment = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Secret" }))
            .Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "hide-out@test.com", "Outsider");
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "hide-viewer@test.com", "Viewer");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "hide-member@test.com", "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, viewer.Tokens.User.Email, "Viewer");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");

        var workspaceId = board.User.Workspace.Id;
        var missingWorkspace = Guid.NewGuid();
        var missingTask = Guid.NewGuid();
        var missingComment = Guid.NewGuid();
        var columnId = board.Todo.Id;

        var routes = new RouteCase[]
        {
            new("GET /workspaces/{id}/projects", HttpMethod.Get, id => $"/api/workspaces/{id}/projects", null, workspaceId, missingWorkspace),
            new("POST /workspaces/{id}/projects", HttpMethod.Post, id => $"/api/workspaces/{id}/projects", new { name = "Hidden", key = "HD" }, workspaceId, missingWorkspace),
            new("GET /workspaces/{id}/members", HttpMethod.Get, id => $"/api/workspaces/{id}/members", null, workspaceId, missingWorkspace),
            new("GET /tasks/{id}", HttpMethod.Get, id => $"/api/tasks/{id}", null, task.Id, missingTask),
            new("PATCH /tasks/{id}", HttpMethod.Patch, id => $"/api/tasks/{id}", new { title = "Hidden" }, task.Id, missingTask),
            new("DELETE /tasks/{id}", HttpMethod.Delete, id => $"/api/tasks/{id}", null, task.Id, missingTask),
            new("POST /tasks/{id}/move", HttpMethod.Post, id => $"/api/tasks/{id}/move", new { toColumnId = columnId, toIndex = 0 }, task.Id, missingTask),
            new("GET /tasks/{id}/comments", HttpMethod.Get, id => $"/api/tasks/{id}/comments", null, task.Id, missingTask),
            new("POST /tasks/{id}/comments", HttpMethod.Post, id => $"/api/tasks/{id}/comments", new { body = "Hidden" }, task.Id, missingTask),
            new("GET /tasks/{id}/activity", HttpMethod.Get, id => $"/api/tasks/{id}/activity", null, task.Id, missingTask),
            new("GET /tasks/{id}/attachments", HttpMethod.Get, id => $"/api/tasks/{id}/attachments", null, task.Id, missingTask),
            new("POST /tasks/{id}/attachments", HttpMethod.Post, id => $"/api/tasks/{id}/attachments", null, task.Id, missingTask),
            new("PATCH /comments/{id}", HttpMethod.Patch, id => $"/api/comments/{id}", new { body = "Hidden" }, commentId, missingComment),
            new("DELETE /comments/{id}", HttpMethod.Delete, id => $"/api/comments/{id}", null, commentId, missingComment),
        };

        foreach (var route in routes)
        {
            var hidden = await Send(outsider.Client, route.Method, route.Url(route.RealId), route.Body);
            var missing = await Send(board.Client, route.Method, route.Url(route.MissingId), route.Body);
            var hiddenBody = await hidden.Content.ReadAsStringAsync();
            var missingBody = await missing.Content.ReadAsStringAsync();

            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{route.Name} non-member: {hiddenBody}");
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{route.Name} missing id: {missingBody}");
            Normalize(hiddenBody, route.RealId).Should().Be(Normalize(missingBody, route.MissingId), route.Name);
            hiddenBody.Should().NotContain("workspace_access_denied");
        }

        (await board.Client.GetAsync($"/api/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertCode(
            await viewer.Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { name = "Nope", key = "NP" }),
            "workspace_access_denied");
        await AssertCode(
            await viewer.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { title = "Nope" }),
            "workspace_access_denied");
        await AssertCode(
            await viewer.Client.PatchAsJsonAsync($"/api/comments/{commentId}", new { body = "Nope" }),
            "workspace_access_denied");
        var notAuthor = await member.Client.PatchAsJsonAsync($"/api/comments/{commentId}", new { body = "Nope" });
        var notAuthorBody = await notAuthor.Content.ReadAsStringAsync();
        notAuthor.StatusCode.Should().Be(HttpStatusCode.Forbidden, notAuthorBody);
        var problem = JsonDocument.Parse(notAuthorBody).RootElement;
        problem.GetProperty("code").GetString().Should().Be("forbidden");
        problem.GetProperty("detail").GetString().Should().Be("Only the author can edit this comment.");
    }

    private static async Task AssertCode(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static string Normalize(string body, Guid id) =>
        body.Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);

    private sealed record RouteCase(
        string Name,
        HttpMethod Method,
        Func<Guid, string> Url,
        object? Body,
        Guid RealId,
        Guid MissingId);
}
