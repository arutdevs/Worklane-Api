using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Worklane.Application.Board;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Maps parent-link database failures onto the stable problem codes so a constraint
/// or a deadlock cannot surface as 500. The service checks the rules first; this is
/// the backstop when a write still hits the database. A delete blocked by children
/// also carries <c>childCount</c>; the database error itself has no count.
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

    /// <summary>
    /// Same map as <see cref="Translate(Exception)"/>. When a parent delete is rejected
    /// because children still point at it, <c>childCount</c> is the direct child rows
    /// in <paramref name="workspaceId"/> (archived or not). A new scope runs the count
    /// so an aborted transaction on the request context cannot turn the 409 into a 500.
    /// A missing workspace, or a write that names a parent outside the project, leaves
    /// the count off.
    /// </summary>
    public static async Task<AppException?> TranslateAsync(
        Exception exception,
        IServiceScopeFactory scopes,
        Guid? workspaceId,
        CancellationToken cancellationToken)
    {
        var mapped = Translate(exception);
        if (mapped is null || mapped.Code != ParentTaskErrors.ParentHasChildren)
            return mapped;
        if (workspaceId is not Guid tenant)
            return mapped;
        // Npgsql redacts the key unless Include Error Detail is on, so the parent id
        // comes from the deleted row EF already has. An update that merely names a
        // missing parent is not a delete, and does not get a count.
        if (!TryGetDeletedTaskId(exception, out var parentId))
            return mapped;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var childCount = await CountDirectChildrenAsync(db, parentId, tenant, cancellationToken);
        return new ConflictException(
            ParentTaskErrors.ParentHasChildren,
            ParentTaskErrors.ParentHasChildrenDetail,
            ErrorParams.Of((ParentTaskErrors.ChildCount, childCount)));
    }

    public static Task<AppException?> TranslateAsync(
        Exception exception,
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        if (services?.GetService<IServiceScopeFactory>() is not { } scopes)
            return Task.FromResult(Translate(exception));

        var workspaceId = services.GetService<ICurrentTenantService>()?.WorkspaceId;
        return TranslateAsync(exception, scopes, workspaceId, cancellationToken);
    }

    private static async Task<int> CountDirectChildrenAsync(
        WorklaneDbContext db,
        Guid parentId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // The task filter hides archived rows, and a permanent delete is blocked by those too.
        // WorkspaceId is the tenant boundary: another workspace's children are not this count.
        return await db.TaskItems
            .IgnoreQueryFilters()
            .CountAsync(
                t => t.ParentTaskId == parentId && t.Project.WorkspaceId == workspaceId,
                cancellationToken);
    }

    private static bool TryGetDeletedTaskId(Exception exception, out Guid parentId)
    {
        parentId = default;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not DbUpdateException update)
                continue;

            var deleted = update.Entries
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity)
                .OfType<TaskItem>()
                .Select(task => task.Id)
                .Distinct()
                .ToList();
            if (deleted.Count != 1)
                return false;

            parentId = deleted[0];
            return true;
        }

        return false;
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
