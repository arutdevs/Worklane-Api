using Worklane.Domain.Common;
using Worklane.Domain.Enums;

namespace Worklane.Domain.Entities;

public class Workspace : IHasTimestamps
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public WorkspacePlan Plan { get; set; } = WorkspacePlan.Free;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = new List<WorkspaceMember>();
    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<Invitation> Invitations { get; set; } = new List<Invitation>();
}
