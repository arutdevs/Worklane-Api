using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Workspaces.Dtos;

namespace Worklane.IntegrationTests.Infrastructure;

public record RegisteredUser(HttpClient Client, AuthTokensResponse Tokens, WorkspaceDto Workspace);

public static class TestAuthHelper
{
    /// <summary>Matches the server's JSON config (camelCase + string enums) so client-side deserialization agrees.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<RegisteredUser> RegisterAsync(HttpClient client, string? email = null, string? displayName = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        displayName ??= "Test User";

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", displayName));
        registerResponse.EnsureSuccessStatusCode();
        var tokens = await registerResponse.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Register did not return tokens.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var workspacesResponse = await client.GetAsync("/api/workspaces");
        workspacesResponse.EnsureSuccessStatusCode();
        var workspaces = await workspacesResponse.Content.ReadFromJsonAsync<List<WorkspaceDto>>(JsonOptions)
            ?? throw new InvalidOperationException("Could not read workspaces.");

        return new RegisteredUser(client, tokens, workspaces.Single());
    }
}
