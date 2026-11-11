using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Workspaces;

public class WorkspaceServiceAcceptInvitationTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public WorkspaceServiceAcceptInvitationTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Accept_WrongAccount_IsNotFoundAndLeavesTheInvite_CorrectAccountSucceeds()
    {
        var owner = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var pendingEmail = $"pending-{Guid.NewGuid():N}@example.com";
        var token = await InvitePendingAsync(owner, pendingEmail, WorkspaceRole.Member);
        var other = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var invitee = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), pendingEmail);
        var unknownToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());

        var before = await ReadInvitationAsync(token);
        var membersBefore = await CountMembersAsync(owner.Workspace.Id);
        var eventsBefore = await CountEventsAsync(owner.Workspace.Id);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWorkspaceService>();

            var wrong = await FluentActions.Awaiting(() =>
                    service.AcceptInvitationAsync(other.Tokens.User.Id, token, CancellationToken.None))
                .Should().ThrowAsync<NotFoundException>();
            var unknown = await FluentActions.Awaiting(() =>
                    service.AcceptInvitationAsync(other.Tokens.User.Id, unknownToken, CancellationToken.None))
                .Should().ThrowAsync<NotFoundException>();

            wrong.Which.Code.Should().Be(unknown.Which.Code).And.Be("not_found");
            wrong.Which.StatusCode.Should().Be(unknown.Which.StatusCode).And.Be(HttpStatusCode.NotFound);
            wrong.Which.Message.Should().Be(unknown.Which.Message).And.Be("Invitation is invalid or has expired.");
        }

        var afterWrong = await ReadInvitationAsync(token);
        afterWrong.Should().BeEquivalentTo(before, options => options.Excluding(invitation => invitation.Workspace));
        afterWrong.AcceptedAt.Should().BeNull();
        (await CountMembersAsync(owner.Workspace.Id)).Should().Be(membersBefore);
        (await CountEventsAsync(owner.Workspace.Id)).Should().Be(eventsBefore);
        (await MemberRoleAsync(owner.Workspace.Id, other.Tokens.User.Id)).Should().BeNull();
        (await MemberRoleAsync(owner.Workspace.Id, invitee.Tokens.User.Id)).Should().BeNull();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWorkspaceService>();
            await service.AcceptInvitationAsync(invitee.Tokens.User.Id, token, CancellationToken.None);
        }

        var afterAccept = await ReadInvitationAsync(token);
        afterAccept.AcceptedAt.Should().NotBeNull();
        (await MemberRoleAsync(owner.Workspace.Id, invitee.Tokens.User.Id)).Should().Be(WorkspaceRole.Member);
        (await MemberRoleAsync(owner.Workspace.Id, other.Tokens.User.Id)).Should().BeNull();
    }

    private async Task<Invitation> ReadInvitationAsync(string token)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.Invitations.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(invitation => invitation.TokenHash == InviteTokens.Hash(token));
    }

    private async Task<int> CountMembersAsync(Guid workspaceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.WorkspaceMembers.IgnoreQueryFilters().CountAsync(member => member.WorkspaceId == workspaceId);
    }

    private async Task<int> CountEventsAsync(Guid workspaceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.WorkspaceMembershipEvents.IgnoreQueryFilters()
            .CountAsync(membershipEvent => membershipEvent.WorkspaceId == workspaceId);
    }

    private async Task<WorkspaceRole?> MemberRoleAsync(Guid workspaceId, Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(member => member.WorkspaceId == workspaceId && member.UserId == userId)
            .Select(member => (WorkspaceRole?)member.Role)
            .SingleOrDefaultAsync();
    }

    private static async Task<string> InvitePendingAsync(RegisteredUser owner, string email, WorkspaceRole role)
    {
        var response = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.Workspace.Id}/invitations",
            new InviteMemberRequest(email, role));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var invitation = JsonSerializer.Deserialize<InvitationDto>(body, TestAuthHelper.JsonOptions);
        var link = invitation!.InviteLink ?? throw new InvalidOperationException("Pending invite did not return a link.");
        const string marker = "token=";
        var start = link.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        return Uri.UnescapeDataString(link[(start + marker.Length)..]);
    }
}
