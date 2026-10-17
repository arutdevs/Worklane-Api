using Worklane.Application.Projects.Dtos;

namespace Worklane.Application.Projects.Interfaces;

public interface IProjectService
{
    Task<List<ProjectDto>> GetProjectsAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<ProjectDto> CreateProjectAsync(Guid workspaceId, Guid createdByUserId, CreateProjectRequest request, CancellationToken cancellationToken);
    Task<ProjectDto> UpdateProjectAsync(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken);
    Task ArchiveProjectAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>Used by RequireWorkspaceRoleAttribute to resolve which workspace a project belongs to.</summary>
    Task<Guid?> GetWorkspaceIdForProjectAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>True when the project exists and is archived. Missing projects are false.</summary>
    Task<bool> IsArchivedAsync(Guid projectId, CancellationToken cancellationToken);
}
