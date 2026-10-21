using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Worklane.Domain.Entities;

namespace Worklane.IntegrationTests.Infrastructure;

/// <summary>
/// Holds the first armed workspace inserts at SaveChanges until every participant
/// has arrived, so two signups pass the slug check and then collide on the unique index.
/// Disarmed (the default) it does nothing.
/// </summary>
public sealed class WorkspaceInsertGate : SaveChangesInterceptor
{
    private int _remaining;
    private int _arrivals;
    private TaskCompletionSource _release = Completed();

    public int Arrivals => Volatile.Read(ref _arrivals);

    public void Arm(int participants)
    {
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _arrivals, 0);
        Volatile.Write(ref _remaining, participants);
    }

    public void Disarm()
    {
        Volatile.Write(ref _remaining, 0);
        _release.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _remaining) > 0
            && eventData.Context is not null
            && HasAddedWorkspace(eventData.Context))
        {
            Interlocked.Increment(ref _arrivals);
            var left = Interlocked.Decrement(ref _remaining);
            if (left == 0)
                _release.TrySetResult();
            if (left >= 0)
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }

    private static bool HasAddedWorkspace(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Workspace>())
        {
            if (entry.State == EntityState.Added)
                return true;
        }

        return false;
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
