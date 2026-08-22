namespace Worklane.Domain.Entities;

public class BoardColumn
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool IsDone { get; set; }

    /// <summary>
    /// Marks the column as "work has started here" — entering it stamps
    /// <see cref="TaskItem.StartedAt"/>, which is where elapsed time is measured from.
    /// </summary>
    public bool IsInProgress { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}
