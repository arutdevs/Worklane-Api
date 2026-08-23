using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Interfaces;

/// <summary>
/// Broadcasts board changes to clients watching a project, over whatever realtime
/// transport is wired up (SignalR in WebApi). Kept as an Application-layer interface so
/// BoardService does not depend on ASP.NET Core SignalR types directly.
/// </summary>
public interface IBoardNotifier
{
    Task TaskCreatedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default);
    Task TaskMovedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default);
    Task TaskUpdatedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default);
}
