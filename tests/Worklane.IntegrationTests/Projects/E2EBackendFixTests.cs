using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Projects;

public class E2EBackendFixTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public E2EBackendFixTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
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
}
