using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class InviteExistingAccountTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public InviteExistingAccountTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invite_ExistingAccount_RequiresAcceptance_AndDoesNotRevealTheAccount(bool emailConfirmed)
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var existing = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var stranger = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await SetEmailConfirmedAsync(existing.Tokens.User.Id, emailConfirmed);

        var known = await InviteAsync(owner, existing.Tokens.User.Email, WorkspaceRole.Member);
        var unknownEmail = $"nobody-{Guid.NewGuid():N}@example.com";
        var unknown = await InviteAsync(owner, unknownEmail, WorkspaceRole.Member);

        known.StatusCode.Should().Be(HttpStatusCode.OK);
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        PropertyNames(known.Body).Should().BeEquivalentTo(PropertyNames(unknown.Body));
        known.Invitation.Id.Should().NotBe(Guid.Empty);
        unknown.Invitation.Id.Should().NotBe(Guid.Empty);
        known.Invitation.InviteLink.Should().NotBeNullOrWhiteSpace();
        unknown.Invitation.InviteLink.Should().NotBeNullOrWhiteSpace();
        known.Invitation.Email.Should().Be(existing.Tokens.User.Email);
        known.Body.Should().NotContain("emailConfirmed");
        known.Body.Should().NotContain("accountExists");
        unknown.Body.Should().NotContain("emailConfirmed");
        await AssertRoleAsync(owner, existing.Tokens.User.Id, null);

        var token = TokenFrom(known.Invitation);
        var wrong = await stranger.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        var missing = await stranger.Client.PostAsJsonAsync(
            "/api/invitations/accept",
            new AcceptInvitationRequest(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        var wrongBody = await wrong.Content.ReadAsStringAsync();
        var missingBody = await missing.Content.ReadAsStringAsync();
        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound, wrongBody);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound, missingBody);
        wrongBody.Should().Be(missingBody);
        await AssertRoleAsync(owner, existing.Tokens.User.Id, null);
        await AssertRoleAsync(owner, stranger.Tokens.User.Id, null);

        var accept = await existing.Client.PostAsJsonAsync("/api/invitations/accept", new AcceptInvitationRequest(token));
        accept.StatusCode.Should().Be(HttpStatusCode.NoContent, await accept.Content.ReadAsStringAsync());
        await AssertRoleAsync(owner, existing.Tokens.User.Id, WorkspaceRole.Member);
    }

    private async Task SetEmailConfirmedAsync(Guid userId, bool confirmed)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.EmailConfirmed.Should().BeFalse();
        if (!confirmed)
            return;

        user.EmailConfirmed = true;
        await db.SaveChangesAsync();
    }

    private static async Task<InviteResult> InviteAsync(RegisteredUser owner, string email, WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, role));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var invitation = JsonSerializer.Deserialize<InvitationDto>(body, TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Invite did not return a body.");
        return new InviteResult(response.StatusCode, body, invitation);
    }

    private static async Task AssertRoleAsync(RegisteredUser owner, Guid userId, WorkspaceRole? expected)
    {
        var members = await owner.Client.GetFromJsonAsync<List<WorkspaceMemberDto>>(
            $"/api/workspaces/{owner.Workspace.Id}/members",
            TestAuthHelper.JsonOptions);
        members!.SingleOrDefault(member => member.UserId == userId)?.Role.Should().Be(expected);
    }

    private static HashSet<string> PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
    }

    private static string TokenFrom(InvitationDto invitation)
    {
        var link = invitation.InviteLink ?? throw new InvalidOperationException("Invite did not return a link.");
        const string marker = "token=";
        var start = link.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        return Uri.UnescapeDataString(link[(start + marker.Length)..]);
    }

    private sealed record InviteResult(HttpStatusCode StatusCode, string Body, InvitationDto Invitation);
}
