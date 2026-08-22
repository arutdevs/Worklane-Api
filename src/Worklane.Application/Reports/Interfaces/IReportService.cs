using Worklane.Application.Reports.Dtos;

namespace Worklane.Application.Reports.Interfaces;

public interface IReportService
{
    /// <summary>
    /// Per-person effort across a workspace, optionally narrowed to one project and to a
    /// date window. The window applies to finished work only — tasks still open always
    /// count, because they describe the situation right now rather than a past period.
    /// </summary>
    Task<WorkspaceReportDto> GetWorkspaceReportAsync(
        Guid workspaceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? projectId,
        CancellationToken ct = default);
}
