using Microsoft.EntityFrameworkCore;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Projects.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services;

public class ProjectService : IProjectService
{
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
            throw UniqueConstraintConflicts.ProjectKeyTaken();

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

        var language = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == createdByUserId)
            .Select(u => u.PreferredLanguage)
            .FirstOrDefaultAsync(cancellationToken);

        foreach (var column in WorkspaceLocale.ColumnsFor(language))
        {
            _db.BoardColumns.Add(new BoardColumn
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Name = column.Name,
                Order = column.Order,
                IsDone = column.IsDone,
                IsInProgress = column.IsInProgress
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

    public async Task<bool> IsArchivedAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var archived = await _db.Projects
            .IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (bool?)p.IsArchived)
            .SingleOrDefaultAsync(cancellationToken);
        return archived == true;
    }
}
