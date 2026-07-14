using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Worklane.Application.Auth.Dtos;
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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
}
