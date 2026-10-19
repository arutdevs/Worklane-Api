using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// Serializes quota check and insert for one workspace. The lock lasts until the
/// current transaction commits, so a second upload sees the first file's bytes.
/// </summary>
internal static class WorkspaceQuotaLock
{
    public static long Key(Guid workspaceId) => BitConverter.ToInt64(workspaceId.ToByteArray(), 0);

    public static Task AcquireAsync(DatabaseFacade database, Guid workspaceId, CancellationToken cancellationToken)
    {
        var key = Key(workspaceId);
        return database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }
}
