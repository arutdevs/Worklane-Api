using Microsoft.EntityFrameworkCore;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Projects.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class ProjectService : IProjectService
{
    private static readonly (string Name, int Order, bool IsDone)[] DefaultColumns =
    [
        ("To Do", 1000, false),
        ("In Progress", 2000, false),
        ("Done", 3000, true),
    ];

    private readonly WorklaneDbContext _db;

    public ProjectService(WorklaneDbContext db)
    {
        _db = db;
    }

    public async Task<List<ProjectDto>> GetProjectsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        return await _db.Projects
            .Where(p => p.WorkspaceId == workspaceId)
            .Select(p => new ProjectDto(p.Id, p.WorkspaceId, p.Name, p.Key, p.Description, p.CreatedById, p.CreatedAt, p.IsArchived))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectDto> CreateProjectAsync(Guid workspaceId, Guid createdByUserId, CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var keyTaken = await _db.Projects
            .IgnoreQueryFilters()
            .AnyAsync(p => p.WorkspaceId == workspaceId && p.Key == request.Key, cancellationToken);

        if (keyTaken)
        {
            throw new ConflictException($"Project key \"{request.Key}\" is already used in this workspace.");
        }

        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = request.Name,
            Key = request.Key,
            Description = request.Description,
            CreatedById = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsArchived = false
        };

        _db.Projects.Add(project);

        // Seed default columns for every new project
        foreach (var (name, order, isDone) in DefaultColumns)
        {
            _db.BoardColumns.Add(new BoardColumn
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Name = name,
                Order = order,
                IsDone = isDone
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new ProjectDto(project.Id, project.WorkspaceId, project.Name, project.Key, project.Description, project.CreatedById, project.CreatedAt, project.IsArchived);
    }

    public async Task<ProjectDto> UpdateProjectAsync(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            throw new NotFoundException(nameof(Project), projectId);
        }

        if (request.Name is not null)
        {
            project.Name = request.Name;
        }
        if (request.Description is not null)
        {
            project.Description = request.Description;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new ProjectDto(project.Id, project.WorkspaceId, project.Name, project.Key, project.Description, project.CreatedById, project.CreatedAt, project.IsArchived);
    }

    public async Task ArchiveProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            throw new NotFoundException(nameof(Project), projectId);
        }

        project.IsArchived = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid?> GetWorkspaceIdForProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        return await _db.Projects
            .IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
