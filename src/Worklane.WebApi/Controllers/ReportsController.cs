using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Worklane.Application.Reports.Dtos;
using Worklane.Application.Reports.Interfaces;
using Worklane.Domain.Enums;
using Worklane.WebApi.Filters;

namespace Worklane.WebApi.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reports;

    public ReportsController(IReportService reports)
    {
        _reports = reports;
    }

    /// <summary>
    /// Effort broken down per workspace member, with the workspace totals alongside it.
    /// Optionally narrowed to one project and to a window of completion dates.
    /// </summary>
    [HttpGet("workspaces/{id:guid}/reports/members")]
    [RequireWorkspaceRole(WorkspaceRole.Viewer)]
    public async Task<ActionResult<WorkspaceReportDto>> GetMemberReport(
        Guid id,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? projectId,
        CancellationToken ct)
    {
        var report = await _reports.GetWorkspaceReportAsync(id, from, to, projectId, ct);
        return Ok(report);
    }
}
