using Npgsql;
using Worklane.Application.Common.Exceptions;

namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Maps a Postgres unique violation (23505) to one 409 problem. Handlers that need a
/// different recovery (the workspace-slug retry) ask <see cref="Is"/> first. Anything
/// that escapes becomes this conflict in the exception middleware, so a double-submit
/// cannot surface as 500.
/// </summary>
public static class UniqueConstraintConflicts
{
    public const string EmailAlreadyRegisteredCode = "email_already_registered";
    public const string EmailAlreadyRegisteredDetail = "An account with this email already exists.";

    public const string ProjectKeyTakenCode = "project_key_taken";
    public const string ProjectKeyTakenDetail = "A project with this key already exists in this workspace.";

    public const string WorkspaceMemberExistsCode = "workspace_member_exists";
    public const string WorkspaceMemberExistsDetail = "This user is already a member of the workspace.";

    public const string InvitationTokenTakenCode = "invitation_token_taken";
    public const string InvitationTokenTakenDetail = "An invitation could not be created. Try again.";

    public const string ProjectMemberExistsCode = "project_member_exists";
    public const string ProjectMemberExistsDetail = "This user is already a member of the project.";

    public const string RefreshTokenConflictCode = "refresh_token_conflict";
    public const string RefreshTokenConflictDetail = "The session could not be saved. Try again.";

    public const string AttachmentStorageKeyTakenCode = "attachment_storage_key_taken";
    public const string AttachmentStorageKeyTakenDetail = "The file could not be stored. Try again.";

    public const string TaskNumberTakenCode = "task_number_taken";
    public const string TaskNumberTakenDetail = "The card number was already used. Try again.";

    public const string WorkspaceSlugUnavailableCode = "workspace_slug_unavailable";
    public const string WorkspaceSlugUnavailableDetail = "A unique workspace address could not be reserved. Try again.";

    public const string RoleNameTakenCode = "role_name_taken";
    public const string RoleNameTakenDetail = "A role with this name already exists.";

    public const string UniqueConflictCode = "unique_conflict";
    public const string UniqueConflictDetail = "The request conflicts with an existing record.";

    /// <summary>
    /// Account name is the email, so both Identity unique indexes are the same conflict.
    /// </summary>
    private static readonly HashSet<string> AccountConstraints = new(StringComparer.Ordinal)
    {
        "EmailIndex",
        "UserNameIndex"
    };

    private static readonly Dictionary<string, (string Code, string Detail)> ByConstraint = new(StringComparer.Ordinal)
    {
        ["IX_Projects_WorkspaceId_Key"] = (ProjectKeyTakenCode, ProjectKeyTakenDetail),
        ["IX_WorkspaceMembers_WorkspaceId_UserId"] = (WorkspaceMemberExistsCode, WorkspaceMemberExistsDetail),
        ["IX_Invitations_TokenHash"] = (InvitationTokenTakenCode, InvitationTokenTakenDetail),
        ["IX_ProjectMembers_ProjectId_UserId"] = (ProjectMemberExistsCode, ProjectMemberExistsDetail),
        ["IX_RefreshTokens_TokenHash"] = (RefreshTokenConflictCode, RefreshTokenConflictDetail),
        ["IX_TaskAttachments_StorageKey"] = (AttachmentStorageKeyTakenCode, AttachmentStorageKeyTakenDetail),
        ["IX_TaskItems_ProjectId_Number"] = (TaskNumberTakenCode, TaskNumberTakenDetail),
        ["IX_Workspaces_Slug"] = (WorkspaceSlugUnavailableCode, WorkspaceSlugUnavailableDetail),
        ["EmailIndex"] = (EmailAlreadyRegisteredCode, EmailAlreadyRegisteredDetail),
        ["UserNameIndex"] = (EmailAlreadyRegisteredCode, EmailAlreadyRegisteredDetail),
        ["RoleNameIndex"] = (RoleNameTakenCode, RoleNameTakenDetail)
    };

    public static IReadOnlyDictionary<string, (string Code, string Detail)> Known => ByConstraint;

    public static ConflictException EmailAlreadyRegistered() =>
        new(EmailAlreadyRegisteredCode, EmailAlreadyRegisteredDetail);

    public static ConflictException ProjectKeyTaken() =>
        new(ProjectKeyTakenCode, ProjectKeyTakenDetail);

    public static ConflictException WorkspaceMemberExists() =>
        new(WorkspaceMemberExistsCode, WorkspaceMemberExistsDetail);

    public static bool Is(Exception exception, string constraintName)
    {
        var postgres = FindUniqueViolation(exception);
        return postgres is not null
            && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
    }

    public static bool IsAccountConstraint(Exception exception)
    {
        var postgres = FindUniqueViolation(exception);
        return postgres?.ConstraintName is { } name && AccountConstraints.Contains(name);
    }

    public static string? ConstraintName(Exception exception) =>
        FindUniqueViolation(exception)?.ConstraintName;

    /// <summary>
    /// Returns a 409 for any unique violation. Unknown constraints, including primary keys,
    /// use <see cref="UniqueConflictCode"/> so the response stays stable and does not echo
    /// the database message.
    /// </summary>
    public static ConflictException? Translate(Exception exception)
    {
        var postgres = FindUniqueViolation(exception);
        if (postgres is null)
            return null;

        if (postgres.ConstraintName is { } name && ByConstraint.TryGetValue(name, out var mapped))
            return new ConflictException(mapped.Code, mapped.Detail);

        return new ConflictException(UniqueConflictCode, UniqueConflictDetail);
    }

    private static PostgresException? FindUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return postgres;
            }
        }

        return null;
    }
}
