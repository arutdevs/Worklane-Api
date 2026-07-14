namespace Worklane.Domain.Entities;

public class ProjectMember
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }

    public Project Project { get; set; } = null!;
}
