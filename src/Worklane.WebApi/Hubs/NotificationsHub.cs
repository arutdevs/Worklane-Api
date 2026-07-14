using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Worklane.WebApi.Hubs;

/// <summary>
/// Realtime notifications hub — wired up now per the Phase 1 scope, with actual
/// board/task broadcast events to be added in a later phase.
/// </summary>
[Authorize]
public class NotificationsHub : Hub
{
}
