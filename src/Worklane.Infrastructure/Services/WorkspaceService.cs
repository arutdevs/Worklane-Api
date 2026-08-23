using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
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
    private readonly FrontendSettings _frontendSettings;

    public WorkspaceService(
        WorklaneDbContext db,
        UserManager<AppUser> userManager,
        ILogger<WorkspaceService> logger,
        IEmailService emailService,
        IOptions<FrontendSettings> frontendSettings)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
        _emailService = emailService;
        _frontendSettings = frontendSettings.Value;
    }

    public async Task<List<WorkspaceDto>> GetMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await _db.WorkspaceMembers
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
        var existingUser = await _userManager.FindByEmailAsync(request.Email);

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
            await _db.SaveChangesAsync(cancellationToken);

            return new InvitationDto(Guid.Empty, request.Email, request.Role, DateTimeOffset.UtcNow, null);
        }

        var invitation = new Invitation
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

        var relativeLink = $"/invitations/accept?token={invitation.Token}";
        var absoluteLink = $"{_frontendSettings.BaseUrl.TrimEnd('/')}{relativeLink}";
        _logger.LogInformation("Invitation created for {Email} to workspace {WorkspaceId}: {InviteLink}", request.Email, workspaceId, absoluteLink);

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

        return new InvitationDto(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, absoluteLink);
    }

    public async Task AcceptInvitationAsync(Guid acceptingUserId, string token, CancellationToken cancellationToken)
    {
        var invitation = await _db.Invitations.SingleOrDefaultAsync(i => i.Token == token, cancellationToken);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new NotFoundException("Invitation is invalid or has expired.");
        }

        var user = await _userManager.FindByIdAsync(acceptingUserId.ToString());
        if (user is null || !string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenAccessException("This invitation was issued to a different email address.");
        }

        var alreadyMember = await _db.WorkspaceMembers
            .AnyAsync(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == acceptingUserId, cancellationToken);

        if (!alreadyMember)
        {
            _db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = invitation.WorkspaceId,
                UserId = acceptingUserId,
                Role = invitation.Role,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }

        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateMemberRoleAsync(Guid workspaceId, Guid targetUserId, WorkspaceRole newRole, CancellationToken cancellationToken)
    {
        var member = await _db.WorkspaceMembers
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetUserId, cancellationToken);

        if (member is null)
        {
            throw new NotFoundException(nameof(WorkspaceMember), targetUserId);
        }

        if (member.Role == WorkspaceRole.Owner && newRole != WorkspaceRole.Owner)
        {
            await EnsureNotLastOwnerAsync(workspaceId, targetUserId, cancellationToken);
        }

        member.Role = newRole;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(Guid workspaceId, Guid targetUserId, CancellationToken cancellationToken)
    {
        var member = await _db.WorkspaceMembers
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetUserId, cancellationToken);

        if (member is null)
        {
            throw new NotFoundException(nameof(WorkspaceMember), targetUserId);
        }

        if (member.Role == WorkspaceRole.Owner)
        {
            await EnsureNotLastOwnerAsync(workspaceId, targetUserId, cancellationToken);
        }

        _db.WorkspaceMembers.Remove(member);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkspaceRole?> GetUserRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var member = await _db.WorkspaceMembers
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);

        return member?.Role;
    }

    private async Task EnsureNotLastOwnerAsync(Guid workspaceId, Guid excludingUserId, CancellationToken cancellationToken)
    {
        var otherOwnerCount = await _db.WorkspaceMembers
            .CountAsync(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Owner && m.UserId != excludingUserId, cancellationToken);

        if (otherOwnerCount == 0)
        {
            throw new ConflictException("Cannot remove or demote the last Owner of a workspace.");
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
