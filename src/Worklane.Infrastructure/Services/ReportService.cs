using Microsoft.EntityFrameworkCore;
using Worklane.Application.Reports.Dtos;
using Worklane.Application.Reports.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly WorklaneDbContext _db;

    public ReportService(WorklaneDbContext db)
    {
        _db = db;
    }

    public async Task<WorkspaceReportDto> GetWorkspaceReportAsync(
        Guid workspaceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? projectId,
        CancellationToken ct = default)
    {
        var projects = _db.Projects.Where(p => p.WorkspaceId == workspaceId);
        if (projectId.HasValue)
            projects = projects.Where(p => p.Id == projectId.Value);

        var projectIds = await projects.Select(p => p.Id).ToListAsync(ct);

        var tasks = await _db.TaskItems
            .Where(t => projectIds.Contains(t.ProjectId) && !t.IsArchived)
            .ToListAsync(ct);

        // The date window only trims finished work. An unfinished task describes the
        // present, so hiding it because it started before `from` would misreport who is
        // currently loaded up.
        if (from.HasValue || to.HasValue)
        {
            tasks = tasks
                .Where(t => t.CompletedAt is not { } done
                    || ((!from.HasValue || done >= from.Value) && (!to.HasValue || done <= to.Value)))
                .ToList();
        }

        var doneColumnIds = await _db.BoardColumns
            .Where(c => projectIds.Contains(c.ProjectId) && c.IsDone)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var inProgressColumnIds = await _db.BoardColumns
            .Where(c => projectIds.Contains(c.ProjectId) && c.IsInProgress)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var members = await _db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId)
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(ct);

        var userIds = members.Select(m => m.UserId)
            .Concat(tasks.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value))
            .Distinct()
            .ToList();

        var users = await _db.Set<AppUser>()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.AvatarUrl })
            .ToDictionaryAsync(u => u.Id, u => (u.DisplayName, u.AvatarUrl), ct);

        var byAssignee = tasks.ToLookup(t => t.AssigneeId);

        var rows = new List<MemberReportDto>();

        foreach (var member in members.OrderBy(m => users.TryGetValue(m.UserId, out var u) ? u.DisplayName : ""))
        {
            var name = users.TryGetValue(member.UserId, out var info) ? info.DisplayName : "(unknown user)";
            var avatar = users.TryGetValue(member.UserId, out var info2) ? info2.AvatarUrl : null;
            rows.Add(Summarise(byAssignee[member.UserId], member.UserId, name, avatar, member.Role,
                doneColumnIds, inProgressColumnIds));
        }

        // Anything nobody owns — usually where work quietly stalls.
        var unassigned = byAssignee[null].ToList();
        if (unassigned.Count > 0)
            rows.Add(Summarise(unassigned, null, "Unassigned", null, null, doneColumnIds, inProgressColumnIds));

        var overall = Summarise(tasks, null, "All", null, null, doneColumnIds, inProgressColumnIds);

        return new WorkspaceReportDto(
            workspaceId, from, to, projectId,
            overall.TotalCount, overall.TodoCount, overall.InProgressCount, overall.DoneCount,
            overall.EstimateDays, overall.ActualDays, overall.VarianceDays,
            overall.OverEstimateCount, overall.OverdueCount, overall.UnestimatedCount,
            overall.OnTimeRate, overall.AvgCycleDays,
            rows);
    }

    private static MemberReportDto Summarise(
        IEnumerable<TaskItem> source,
        Guid? userId,
        string displayName,
        string? avatarUrl,
        WorkspaceRole? role,
        List<Guid> doneColumnIds,
        List<Guid> inProgressColumnIds)
    {
        var items = source as IList<TaskItem> ?? source.ToList();

        int done = 0, inProgress = 0, todo = 0;
        int overEstimate = 0, overdue = 0, overdueDays = 0, unestimated = 0;
        double estimateDays = 0, actualDays = 0;
        int onTimeEligible = 0, onTime = 0;
        var cycleTimes = new List<double>();

        foreach (var t in items)
        {
            if (doneColumnIds.Contains(t.ColumnId)) done++;
            else if (inProgressColumnIds.Contains(t.ColumnId)) inProgress++;
            else todo++;

            if (t.EstimateDays is { } est) estimateDays += est;
            else unestimated++;

            var (actual, isOver, _) = BoardService.CalculateEffort(t);
            if (actual is { } spent)
            {
                actualDays += spent;
                if (t.CompletedAt is not null) cycleTimes.Add(spent);
            }
            if (isOver) overEstimate++;

            var (isOverdue, days) = BoardService.CalculateOverdue(t);
            if (isOverdue)
            {
                overdue++;
                overdueDays += days;
            }

            // Only finished, due-dated work can be judged on punctuality.
            if (t.DueDate is not null && t.CompletedAt is { } completed)
            {
                onTimeEligible++;
                if (completed <= t.DueDate) onTime++;
            }
        }

        return new MemberReportDto(
            userId, displayName, avatarUrl, role,
            todo, inProgress, done, items.Count,
            Math.Round(estimateDays, 2),
            Math.Round(actualDays, 2),
            Math.Round(actualDays - estimateDays, 2),
            overEstimate, overdue, overdueDays, unestimated,
            onTimeEligible == 0 ? null : Math.Round(onTime * 100.0 / onTimeEligible, 1),
            cycleTimes.Count == 0 ? null : Math.Round(cycleTimes.Average(), 2));
    }
}
