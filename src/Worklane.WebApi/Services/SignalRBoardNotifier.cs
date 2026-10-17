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

    public Task CommentCreatedAsync(Guid projectId, CommentBroadcastDto comment, CancellationToken ct = default) =>
        Group(projectId).SendAsync("CommentCreated", comment, ct);

    public Task CommentUpdatedAsync(Guid projectId, CommentBroadcastDto comment, CancellationToken ct = default) =>
        Group(projectId).SendAsync("CommentUpdated", comment, ct);

    public Task CommentDeletedAsync(Guid projectId, CommentDeletedEvent payload, CancellationToken ct = default) =>
        Group(projectId).SendAsync("CommentDeleted", payload, ct);

    public Task AttachmentAddedAsync(Guid projectId, AttachmentBroadcastDto attachment, CancellationToken ct = default) =>
        Group(projectId).SendAsync("AttachmentAdded", attachment, ct);

    public Task AttachmentDeletedAsync(Guid projectId, AttachmentDeletedEvent payload, CancellationToken ct = default) =>
        Group(projectId).SendAsync("AttachmentDeleted", payload, ct);

    private IClientProxy Group(Guid projectId) => _hub.Clients.Group(NotificationsHub.GroupName(projectId));
}
