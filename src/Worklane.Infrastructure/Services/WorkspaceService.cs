using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Workspaces;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly WorklaneDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly IEmailService _emailService;
    private readonly IRealtimeGroupMembership _realtime;
    private readonly FrontendSettings _frontendSettings;

    public WorkspaceService(
        WorklaneDbContext db,
        UserManager<AppUser> userManager,
        ILogger<WorkspaceService> logger,
        IEmailService emailService,
        IRealtimeGroupMembership realtime,
        IOptions<FrontendSettings> frontendSettings)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
        _emailService = emailService;
        _realtime = realtime;
        _frontendSettings = frontendSettings.Value;
    }

    public async Task<List<WorkspaceDto>> GetMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await _db.WorkspaceMembers
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .Include(m => m.Workspace)
            .ToListAsync(cancellationToken);

        return memberships
            .Select(m => new WorkspaceDto(m.Workspace.Id, m.Workspace.Name, m.Workspace.Slug, m.Workspace.Plan, m.Role, m.Workspace.CreatedAt))
            .ToList();
    }

    public async Task<WorkspaceDto> CreateWorkspaceAsync(Guid ownerUserId, CreateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var slug = await GenerateUniqueSlugAsync(request.Name, cancellationToken);

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Slug = slug,
            Plan = WorkspacePlan.Free,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Workspaces.Add(workspace);

        _db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = ownerUserId,
            Role = WorkspaceRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new WorkspaceDto(workspace.Id, workspace.Name, workspace.Slug, workspace.Plan, WorkspaceRole.Owner, workspace.CreatedAt);
    }

    public async Task<List<WorkspaceMemberDto>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var members = await _db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var userIds = members.Select(m => m.UserId).ToList();
        var users = await _userManager.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        return members
            .Where(m => users.ContainsKey(m.UserId))
            .Select(m =>
            {
                var user = users[m.UserId];
                return new WorkspaceMemberDto(user.Id, user.Email!, user.DisplayName, user.AvatarUrl, m.Role, m.JoinedAt);
            })
            .ToList();
    }

    public async Task<InvitationDto> InviteMemberAsync(Guid workspaceId, Guid invitedByUserId, InviteMemberRequest request, CancellationToken cancellationToken)
    {
        // Identity lookup stays outside the membership lock. Clear() inside the lock detaches
        // tracked users; the id and email on this instance are still valid afterwards.
        var existingUser = await _userManager.FindByEmailAsync(request.Email);

        Invitation? invitation = null;
        string? absoluteLink = null;
        await using (var transaction = await BeginLockedMembershipChangeAsync(workspaceId, cancellationToken))
        {
            var actor = await RequireActorAsync(workspaceId, invitedByUserId, cancellationToken);
            WorkspaceMembershipPolicy.EnsureCanInvite(actor.Role, request.Role);

            if (existingUser is not null)
            {
                var alreadyMember = await _db.WorkspaceMembers
                    .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == existingUser.Id, cancellationToken);

                if (alreadyMember)
                {
                    throw new ConflictException("This user is already a member of the workspace.");
                }

                _db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    UserId = existingUser.Id,
                    Role = request.Role,
                    JoinedAt = DateTimeOffset.UtcNow
                });
                AddMembershipEvent(workspaceId, invitedByUserId, existingUser.Id, WorkspaceMembershipAction.MemberAdded, oldRole: null, request.Role);
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                LogMembershipChange(workspaceId, invitedByUserId, existingUser.Id, WorkspaceMembershipAction.MemberAdded, oldRole: null, request.Role);

                return new InvitationDto(Guid.Empty, request.Email, request.Role, DateTimeOffset.UtcNow, null);
            }

            invitation = new Invitation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Email = request.Email,
                Role = request.Role,
                Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                InvitedById = invitedByUserId,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Invitations.Add(invitation);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var relativeLink = $"/invitations/accept?token={invitation.Token}";
            absoluteLink = $"{_frontendSettings.BaseUrl.TrimEnd('/')}{relativeLink}";
            _logger.LogInformation("Invitation created for {Email} to workspace {WorkspaceId}: {InviteLink}", request.Email, workspaceId, absoluteLink);
        }

        // Best-effort: a workspace member is invited either way, and the link above is
        // logged, so a delivery failure (no SMTP credentials configured, provider outage)
        // should not fail invite creation — it just falls back to sharing the link by hand.
        try
        {
            var workspaceName = await _db.Workspaces
                .Where(w => w.Id == workspaceId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? "Worklane";

            var html = $"""
                <p>You've been invited to join <strong>{System.Net.WebUtility.HtmlEncode(workspaceName)}</strong> on Worklane.</p>
                <p><a href="{absoluteLink}">Accept the invitation</a></p>
                <p style="color:#64748b;font-size:.875rem;">This link expires on {invitation.ExpiresAt:MMM d, yyyy}.</p>
                """;

            await _emailService.SendAsync(invitation.Email, $"You're invited to {workspaceName} on Worklane", html, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send invitation email to {Email}; the link above still works", request.Email);
        }

        return new InvitationDto(invitation!.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, absoluteLink);
    }

    public async Task AcceptInvitationAsync(Guid acceptingUserId, string token, CancellationToken cancellationToken)
    {
        // Accept runs with no tenant pinned. IgnoreQueryFilters keeps the lookup working
        // when the membership filter is fail-closed.
        var invitation = await _db.Invitations
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(i => i.Token == token, cancellationToken);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new NotFoundException("Invitation is invalid or has expired.");
        }

        var user = await _userManager.FindByIdAsync(acceptingUserId.ToString());
        if (user is null || !string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenAccessException("This invitation was issued to a different email address.");
        }

        var workspaceId = invitation.WorkspaceId;
        await using var transaction = await BeginLockedMembershipChangeAsync(workspaceId, cancellationToken);

        invitation = await _db.Invitations
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(i => i.Token == token, cancellationToken);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new NotFoundException("Invitation is invalid or has expired.");
        }

        var inviterRole = await _db.WorkspaceMembers
            .IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == invitation.InvitedById)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);

        if (inviterRole is null)
            throw new ForbiddenAccessException("The person who sent this invitation can no longer add members.");

        WorkspaceMembershipPolicy.EnsureCanInvite(inviterRole.Value, invitation.Role);

        var alreadyMember = await _db.WorkspaceMembers
            .IgnoreQueryFilters()
            .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == acceptingUserId, cancellationToken);

        var added = false;
        if (!alreadyMember)
        {
            _db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = acceptingUserId,
                Role = invitation.Role,
                JoinedAt = DateTimeOffset.UtcNow
            });
            AddMembershipEvent(workspaceId, invitation.InvitedById, acceptingUserId, WorkspaceMembershipAction.MemberAdded, oldRole: null, invitation.Role);
            added = true;
        }

        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (added)
        {
            LogMembershipChange(workspaceId, invitation.InvitedById, acceptingUserId, WorkspaceMembershipAction.MemberAdded, oldRole: null, invitation.Role);
        }
    }

    public async Task UpdateMemberRoleAsync(Guid workspaceId, Guid actorUserId, Guid targetUserId, WorkspaceRole newRole, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginLockedMembershipChangeAsync(workspaceId, cancellationToken);
        var actor = await RequireActorAsync(workspaceId, actorUserId, cancellationToken);
        var member = await _db.WorkspaceMembers
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetUserId, cancellationToken);

        if (member is null)
        {
            throw new NotFoundException(nameof(WorkspaceMember), targetUserId);
        }

        // Authorize before the last-Owner guard. An Admin targeting the sole Owner is 403, not 409.
        WorkspaceMembershipPolicy.EnsureCanManage(
            actor.Role, member.Role, newRole, isSelf: actor.UserId == member.UserId);

        if (member.Role == newRole)
            return;

        if (member.Role == WorkspaceRole.Owner && newRole != WorkspaceRole.Owner)
        {
            await EnsureNotLastOwnerAsync(workspaceId, targetUserId, cancellationToken);
        }

        var oldRole = member.Role;
        member.Role = newRole;
        AddMembershipEvent(workspaceId, actorUserId, targetUserId, WorkspaceMembershipAction.RoleChanged, oldRole, newRole);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogMembershipChange(workspaceId, actorUserId, targetUserId, WorkspaceMembershipAction.RoleChanged, oldRole, newRole);
    }

    public async Task RemoveMemberAsync(Guid workspaceId, Guid actorUserId, Guid targetUserId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginLockedMembershipChangeAsync(workspaceId, cancellationToken);
        var actor = await RequireActorAsync(workspaceId, actorUserId, cancellationToken);
        var member = await _db.WorkspaceMembers
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetUserId, cancellationToken);

        if (member is null)
        {
            throw new NotFoundException(nameof(WorkspaceMember), targetUserId);
        }

        // Authorize before the last-Owner guard. An Admin targeting the sole Owner is 403, not 409.
        WorkspaceMembershipPolicy.EnsureCanManage(
            actor.Role, member.Role, newRole: null, isSelf: actor.UserId == member.UserId);

        if (member.Role == WorkspaceRole.Owner)
        {
            await EnsureNotLastOwnerAsync(workspaceId, targetUserId, cancellationToken);
        }

        var oldRole = member.Role;
        AddMembershipEvent(workspaceId, actorUserId, targetUserId, WorkspaceMembershipAction.MemberRemoved, oldRole, newRole: null);
        _db.WorkspaceMembers.Remove(member);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // After commit: a rollback must not drop SignalR groups for a member who is still in the workspace.
        await _realtime.RemoveUserFromWorkspaceAsync(workspaceId, targetUserId, cancellationToken);
        LogMembershipChange(workspaceId, actorUserId, targetUserId, WorkspaceMembershipAction.MemberRemoved, oldRole, newRole: null);
    }

    public async Task<WorkspaceRole?> GetUserRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        // Runs before the request pins a tenant (and from the hub, which never does),
        // so the fail-closed filter would hide every membership row.
        var member = await _db.WorkspaceMembers
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);

        return member?.Role;
    }

    /// <summary>
    /// Role comes from this workspace's membership row. The route filter has already pinned
    /// that workspace, and the predicate is the same id.
    /// </summary>
    private async Task<WorkspaceMember> RequireActorAsync(Guid workspaceId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var actor = await _db.WorkspaceMembers
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == actorUserId, cancellationToken);

        if (actor is null)
            throw new ForbiddenAccessException(
                "member_manage_forbidden",
                "You do not have permission to manage workspace members.");

        return actor;
    }

    private void AddMembershipEvent(
        Guid workspaceId,
        Guid actorUserId,
        Guid targetUserId,
        WorkspaceMembershipAction action,
        WorkspaceRole? oldRole,
        WorkspaceRole? newRole)
    {
        _db.WorkspaceMembershipEvents.Add(new WorkspaceMembershipEvent
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            Action = action,
            OldRole = oldRole,
            NewRole = newRole,
            OccurredAt = DateTimeOffset.UtcNow
        });
    }

    private void LogMembershipChange(
        Guid workspaceId,
        Guid actorUserId,
        Guid targetUserId,
        WorkspaceMembershipAction action,
        WorkspaceRole? oldRole,
        WorkspaceRole? newRole)
    {
        _logger.LogInformation(
            "Workspace membership {Action} in {WorkspaceId}: actor {ActorUserId} target {TargetUserId} role {OldRole} -> {NewRole}",
            action,
            workspaceId,
            actorUserId,
            targetUserId,
            oldRole?.ToString() ?? "none",
            newRole?.ToString() ?? "removed");
    }

    /// <summary>
    /// Serializes membership writes for one workspace until the transaction ends.
    /// The role filter may already have tracked the actor in this request; drop that
    /// so the next read sees commits that waited on the lock.
    /// </summary>
    private async Task<IDbContextTransaction> BeginLockedMembershipChangeAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockWorkspaceMembersAsync(workspaceId, cancellationToken);
            _db.ChangeTracker.Clear();
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// hashtext returns integer. The single-key advisory lock takes bigint.
    /// </summary>
    private async Task LockWorkspaceMembersAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Workspace membership changes require a transaction.");
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@workspaceId)::bigint)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "workspaceId";
        parameter.Value = workspaceId.ToString();
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Caller holds the workspace advisory lock and has re-read membership after taking it.
    /// </summary>
    private async Task EnsureNotLastOwnerAsync(Guid workspaceId, Guid excludingUserId, CancellationToken cancellationToken)
    {
        var otherOwnerCount = await _db.WorkspaceMembers
            .CountAsync(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Owner && m.UserId != excludingUserId, cancellationToken);

        if (otherOwnerCount == 0)
        {
            throw new ConflictException(
                "last_owner",
                "Cannot remove or demote the last Owner of a workspace.");
        }
    }

    private async Task<string> GenerateUniqueSlugAsync(string baseName, CancellationToken cancellationToken)
    {
        var lowered = baseName.ToLowerInvariant();
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--"))
        {
            collapsed = collapsed.Replace("--", "-");
        }
        var slug = collapsed.Trim('-');
        var candidate = slug;
        var suffix = 0;

        while (await _db.Workspaces.AnyAsync(w => w.Slug == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{slug}-{suffix}";
        }

        return candidate;
    }
}
