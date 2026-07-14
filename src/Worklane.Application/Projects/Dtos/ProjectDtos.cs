namespace Worklane.Application.Projects.Dtos;

public record ProjectDto(Guid Id, Guid WorkspaceId, string Name, string Key, string? Description, Guid CreatedById, DateTimeOffset CreatedAt, bool IsArchived);

public record CreateProjectRequest(string Name, string Key, string? Description);

public record UpdateProjectRequest(string? Name, string? Description);
