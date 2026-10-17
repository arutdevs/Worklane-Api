using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Interfaces;

public interface ITaskActivityService
{
    Task<IReadOnlyList<TaskActivityDto>> GetActivityAsync(Guid taskId, CancellationToken cancellationToken = default);
}
