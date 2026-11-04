using Worklane.Application.Imports.Dtos;
using Worklane.Domain.Enums;

namespace Worklane.Application.Imports.Interfaces;

public interface IProjectExportService
{
    Task<ProjectExportFile> ExportAsync(
        Guid projectId,
        Guid userId,
        ExportFormat format,
        CancellationToken cancellationToken);
}
