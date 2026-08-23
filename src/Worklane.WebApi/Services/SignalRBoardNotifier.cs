using Microsoft.AspNetCore.SignalR;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.WebApi.Hubs;

namespace Worklane.WebApi.Services;

public class SignalRBoardNotifier : IBoardNotifier
{
    private readonly IHubContext<NotificationsHub> _hub;

    public SignalRBoardNotifier(IHubContext<NotificationsHub> hub)
    {
        _hub = hub;
    }

    public Task TaskCreatedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default) =>
        Group(projectId).SendAsync("TaskCreated", task, ct);

    public Task TaskMovedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default) =>
        Group(projectId).SendAsync("TaskMoved", task, ct);

    public Task TaskUpdatedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default) =>
        Group(projectId).SendAsync("TaskUpdated", task, ct);

    private IClientProxy Group(Guid projectId) => _hub.Clients.Group(NotificationsHub.GroupName(projectId));
}
