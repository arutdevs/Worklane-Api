using Microsoft.EntityFrameworkCore;
using Worklane.Application.Board.Dtos;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

internal static class UserLookup
{
    public static async Task<Dictionary<Guid, string>> DisplayNamesAsync(
        WorklaneDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.Set<AppUser>()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }

    public static UserRef Ref(IReadOnlyDictionary<Guid, string> names, Guid userId) =>
        new(userId, names.TryGetValue(userId, out var name) ? name : string.Empty);
}
