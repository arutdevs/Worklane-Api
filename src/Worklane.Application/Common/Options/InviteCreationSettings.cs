namespace Worklane.Application.Common.Options;

/// <summary>
/// Limits for <c>POST /api/workspaces/{id}/invitations</c>. The rate-limit policy is a
/// fixed window partitioned by the access-token subject. <see cref="MaxPendingInvites"/>
/// is how many unaccepted, unexpired invitations one workspace may hold.
/// </summary>
public class InviteCreationSettings
{
    public const string SectionName = "InviteCreation";

    public const string PolicyName = "invite-create";

    public int PermitLimit { get; set; } = 20;

    public int WindowSeconds { get; set; } = 3600;

    public int MaxPendingInvites { get; set; } = 50;
}
