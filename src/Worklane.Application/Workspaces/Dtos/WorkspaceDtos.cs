using Worklane.Domain.Enums;

namespace Worklane.Application.Workspaces.Dtos;

public record WorkspaceDto(Guid Id, string Name, string Slug, WorkspacePlan Plan, WorkspaceRole MyRole, DateTimeOffset CreatedAt);

public record CreateWorkspaceRequest(string Name);

public record WorkspaceMemberDto(Guid UserId, string Email, string DisplayName, string? AvatarUrl, WorkspaceRole Role, DateTimeOffset JoinedAt);

public record InviteMemberRequest(string Email, WorkspaceRole Role);

public record InvitationDto(Guid Id, string Email, WorkspaceRole Role, DateTimeOffset ExpiresAt, string? InviteLink);

public record AcceptInvitationRequest(string Token);

public record UpdateMemberRoleRequest(WorkspaceRole Role);
