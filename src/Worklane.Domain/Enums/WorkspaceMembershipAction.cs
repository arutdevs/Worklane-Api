namespace Worklane.Domain.Enums;

/// <summary>
/// What a <see cref="Entities.WorkspaceMembershipEvent"/> row records. Stored as a string.
/// There is no database default: the first member is 0, and a default would make EF
/// drop an explicit <see cref="RoleChanged"/> from the INSERT.
/// </summary>
public enum WorkspaceMembershipAction
{
    RoleChanged,
    MemberRemoved,
    MemberAdded
}
