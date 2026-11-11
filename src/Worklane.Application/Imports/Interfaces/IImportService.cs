using Worklane.Application.Imports.Dtos;

namespace Worklane.Application.Imports.Interfaces;

public interface IImportService
{
    Task<ImportBatchDto> UploadAsync(
        Guid projectId,
        Guid userId,
        string? preset,
        string? fileName,
        Stream content,
        CancellationToken cancellationToken);

    Task<ImportBatchDto> UpdateMappingAsync(
        Guid projectId,
        Guid importId,
        UpdateImportMappingRequest request,
        CancellationToken cancellationToken);

    Task<ImportPreviewDto> PreviewAsync(Guid projectId, Guid importId, CancellationToken cancellationToken);

    Task<ImportCommitDto> CommitAsync(
        Guid projectId,
        Guid importId,
        Guid userId,
        CommitImportRequest request,
        CancellationToken cancellationToken);

    Task<ImportPreviewDto> GetAsync(Guid projectId, Guid importId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ImportBatchDto>> ListAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ImportUndoDto> UndoAsync(Guid projectId, Guid importId, CancellationToken cancellationToken);
}
