using Npgsql;
using Worklane.Application.Board;
using Worklane.Application.Common.Exceptions;

namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Maps parent-link database failures onto the stable problem codes so a constraint
/// or a deadlock cannot surface as 500. The service checks the rules first; this is
/// the backstop when a write still hits the database.
/// </summary>
public static class ParentLinkConflicts
{
    public const string ForeignKeyConstraint = "FK_TaskItems_TaskItems_ProjectId_ParentTaskId";
    public const string CheckConstraint = "CK_TaskItems_NotSelfParent";

    public static AppException? Translate(Exception exception)
    {
        var postgres = FindPostgres(exception);
        if (postgres is null)
            return null;

        if (postgres.SqlState == PostgresErrorCodes.CheckViolation
            && postgres.ConstraintName == CheckConstraint)
        {
            return new BadRequestException(ParentTaskErrors.ParentSelf, ParentTaskErrors.ParentSelfDetail);
        }

        if (postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation
            && postgres.ConstraintName == ForeignKeyConstraint)
        {
            return new ConflictException(ParentTaskErrors.ParentHasChildren, ParentTaskErrors.ParentHasChildrenDetail);
        }

        // 40P01 deadlock_detected, 40001 serialization_failure. Lock timeout (55P03) is
        // not mapped: these transactions do not set lock_timeout.
        if (postgres.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
        {
            return new ConflictException(ParentTaskErrors.ParentLinkConflict, ParentTaskErrors.ParentLinkConflictDetail);
        }

        return null;
    }

    private static PostgresException? FindPostgres(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres;
        }

        return null;
    }
}
