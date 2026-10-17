using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class TaskActivityService : ITaskActivityService
{
    private readonly WorklaneDbContext _db;

    public TaskActivityService(WorklaneDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<TaskActivityDto>> GetActivityAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var exists = await _db.TaskItems.AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!exists)
            throw new NotFoundException(nameof(TaskItem), taskId);

        var rows = await _db.TaskActivities
            .Where(a => a.TaskId == taskId)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(cancellationToken);

        var names = await UserLookup.DisplayNamesAsync(_db, rows.Select(a => a.ActorUserId), cancellationToken);

        return rows.Select(a => new TaskActivityDto(
            a.Id,
            a.TaskId,
            a.Type,
            UserLookup.Ref(names, a.ActorUserId),
            a.CreatedAt,
            ParseDetails(a.Details))).ToList();
    }

    private static JsonElement? ParseDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
