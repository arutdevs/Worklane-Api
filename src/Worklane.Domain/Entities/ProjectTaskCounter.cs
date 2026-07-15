namespace Worklane.Domain.Entities;

/// <summary>
/// One row per project. Used as a PostgreSQL-level atomic counter to assign
/// sequential TaskItem.Number values without gaps or races.
/// </summary>
public class ProjectTaskCounter
{
    public Guid ProjectId { get; set; }
    public int LastNumber { get; set; }

    public Project Project { get; set; } = null!;
}
