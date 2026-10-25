using Microsoft.EntityFrameworkCore;

namespace Worklane.Infrastructure.Persistence;

internal static class DbRetry
{
    /// <summary>
    /// A retrying provider rejects a user transaction unless the whole unit runs inside
    /// the strategy. With retries off, the delegate runs directly.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        WorklaneDbContext db,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        if (strategy.RetriesOnFailure)
            return await strategy.ExecuteAsync(action, cancellationToken);

        return await action(cancellationToken);
    }
}
