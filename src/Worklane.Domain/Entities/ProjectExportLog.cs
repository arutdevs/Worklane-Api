using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

/// <summary>Audit row for one project export. The file itself is not stored.</summary>
public class ProjectExportLog
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ActorUserId { get; set; }
    public ExportFormat Format { get; set; }
    public int RowCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Project Project { get; set; } = null!;
}
