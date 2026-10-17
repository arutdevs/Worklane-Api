using Worklane.Domain.Enums;

namespace Worklane.Application.Reports.Dtos;

/// <summary>
/// One row of the per-person breakdown. Every workspace member gets a row even with no
/// tasks, so the roster reads as a team rather than as whoever happened to be assigned
/// something. Work with no assignee is reported under a row with a null <see cref="UserId"/>.
/// </summary>
public record MemberReportDto(
    Guid? UserId,
    string DisplayName,
    string? AvatarUrl,
    WorkspaceRole? Role,

    // Volume
    int TodoCount,
    int InProgressCount,
    int DoneCount,
    int TotalCount,

    // Man-days
    double EstimateDays,
    double ActualDays,
    /// <summary>Actual minus estimate. Positive means the work ran long.</summary>
    double VarianceDays,

    // Quality
    int OverEstimateCount,
    int OverdueCount,
    int OverdueDays,
    int UnestimatedCount,
    /// <summary>Share of finished, due-dated tasks delivered on time. Null when there are none to judge.</summary>
    double? OnTimeRate,
    /// <summary>Mean working days from start to done. Null until something is finished.</summary>
    double? AvgCycleDays
);

/// <summary>
/// Workspace-wide totals plus the per-person rows behind them.
/// Actual man-days count Monday–Friday only: work done on a weekend counts as 0
/// working days, and there is no holiday calendar. The figure is elapsed time, not
/// effort — several cards in progress at once each accrue the full duration.
/// </summary>
public record WorkspaceReportDto(
    Guid WorkspaceId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? ProjectId,

    int TotalCount,
    int TodoCount,
    int InProgressCount,
    int DoneCount,

    double EstimateDays,
    double ActualDays,
    double VarianceDays,

    int OverEstimateCount,
    int OverdueCount,
    int UnestimatedCount,
    double? OnTimeRate,
    double? AvgCycleDays,

    IReadOnlyList<MemberReportDto> Members
);
