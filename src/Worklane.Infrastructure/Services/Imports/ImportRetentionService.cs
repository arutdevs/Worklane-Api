using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services.Imports;

/// <summary>
/// Deletes import files whose retention window has passed, and files that a commit or undo
/// left behind. There is no endpoint. The sweep has no tenant pinned, so it cannot use the
/// query filters; it never returns a batch to a caller.
/// </summary>
public sealed class ImportRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ImportRetentionService> _logger;

    public ImportRetentionService(IServiceScopeFactory scopes, ILogger<ImportRetentionService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PurgeAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Import retention failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var now = DateTimeOffset.UtcNow;
        var batches = await db.ImportBatches
            .IgnoreQueryFilters()
            .Where(b => b.StorageKey != null && (b.ExpiresAt <= now || b.Status == ImportBatchStatus.Committed || b.Status == ImportBatchStatus.Undone))
            .ToListAsync(cancellationToken);
        if (batches.Count == 0)
            return;

        var ids = batches.Select(b => b.Id).ToList();
        var rows = await db.ImportBatchRows
            .IgnoreQueryFilters()
            .Where(r => ids.Contains(r.ImportBatchId))
            .ToListAsync(cancellationToken);
        var rowsByBatch = rows.GroupBy(r => r.ImportBatchId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var batch in batches)
        {
            if (batch.StorageKey is { } key)
            {
                await files.DeleteAsync(key, cancellationToken);
                batch.StorageKey = null;
                batch.FileDeletedAt = now;
            }

            batch.SampleJson = null;
            if (batch.Status is ImportBatchStatus.Uploaded or ImportBatchStatus.Mapped or ImportBatchStatus.Previewed)
                batch.Status = ImportBatchStatus.Expired;
            if (!rowsByBatch.TryGetValue(batch.Id, out var batchRows))
                continue;
            foreach (var row in batchRows)
            {
                row.Title = null;
                row.Description = null;
                row.ColumnName = null;
                row.AssigneeEmail = null;
                row.PriorityText = null;
                row.DueDateText = null;
                row.EstimateText = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
