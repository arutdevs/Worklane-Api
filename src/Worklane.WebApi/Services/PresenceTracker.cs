using System.Collections.Concurrent;

namespace Worklane.WebApi.Services;

public sealed record PresenceLeaveResult(Guid ProjectId, Guid UserId, bool WasLastConnection, Guid? EditingTaskId);

/// <summary>
/// In-memory presence for one process. Group membership (which connection is in which
/// project) is stored separately from who is online and what they are editing, so one
/// connection can join several projects without forgetting the earlier ones.
/// </summary>
public interface IPresenceTracker
{
    /// <summary>Registers a connection under a project. Returns true when this is that
    /// user's first live connection to the project.</summary>
    bool Join(Guid projectId, Guid userId, string connectionId);

    /// <summary>Drops the connection from every project. Used when the socket closes.</summary>
    IReadOnlyList<PresenceLeaveResult> Leave(string connectionId);

    /// <summary>Drops the connection from one project and leaves its other projects in place.</summary>
    PresenceLeaveResult? LeaveProject(string connectionId, Guid projectId);

    /// <summary>Sets (or clears, with null) which task a connection is editing. Returns the
    /// previously-editing task id, if any.</summary>
    Guid? SetEditing(string connectionId, Guid? taskId);

    /// <summary>The project this connection most recently joined, if it is still in that project.</summary>
    Guid? ProjectFor(string connectionId);

    IReadOnlyCollection<Guid> OnlineUserIds(Guid projectId);

    /// <summary>Connections for this user that currently include the project. Reads the
    /// membership maps, not the presence map.</summary>
    IReadOnlyCollection<string> ConnectionIds(Guid projectId, Guid userId);
}

public class PresenceTracker : IPresenceTracker
{
    // Membership, kept apart from presence.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _userConnections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _connectionProjects = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionUsers = new();

    // Which project editing and "current project" follow. Not used to decide group removal.
    private readonly ConcurrentDictionary<string, Guid> _focusProject = new();
    private readonly ConcurrentDictionary<string, Guid?> _editing = new();
    private readonly ConcurrentDictionary<string, Guid> _editingProject = new();

    // Presence: who is online on a project.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>>> _projectUsers = new();

    public bool Join(Guid projectId, Guid userId, string connectionId)
    {
        _connectionUsers[connectionId] = userId;
        var projects = _connectionProjects.GetOrAdd(connectionId, _ => new ConcurrentDictionary<Guid, byte>());
        projects[projectId] = 0;
        var userConnections = _userConnections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
        userConnections[connectionId] = 0;
        _focusProject[connectionId] = projectId;

        var users = _projectUsers.GetOrAdd(projectId, _ => new ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>>());
        var connections = users.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
        var added = connections.TryAdd(connectionId, 0);
        return added && connections.Count == 1;
    }

    public IReadOnlyList<PresenceLeaveResult> Leave(string connectionId)
    {
        if (!_connectionProjects.TryGetValue(connectionId, out var projects))
            return [];

        var results = new List<PresenceLeaveResult>();
        foreach (var projectId in projects.Keys.ToArray())
        {
            var left = LeaveProject(connectionId, projectId);
            if (left is not null)
                results.Add(left);
        }

        return results;
    }

    public PresenceLeaveResult? LeaveProject(string connectionId, Guid projectId)
    {
        if (!_connectionUsers.TryGetValue(connectionId, out var userId))
            return null;
        if (!_connectionProjects.TryGetValue(connectionId, out var projects) || !projects.TryRemove(projectId, out _))
            return null;

        if (projects.IsEmpty)
        {
            _connectionProjects.TryRemove(connectionId, out _);
            _connectionUsers.TryRemove(connectionId, out _);
            _focusProject.TryRemove(connectionId, out _);
            if (_userConnections.TryGetValue(userId, out var userConnections))
            {
                userConnections.TryRemove(connectionId, out _);
                if (userConnections.IsEmpty)
                    _userConnections.TryRemove(userId, out _);
            }
        }
        else if (_focusProject.TryGetValue(connectionId, out var focus) && focus == projectId)
        {
            _focusProject[connectionId] = projects.Keys.First();
        }

        Guid? editing = null;
        if (_editingProject.TryGetValue(connectionId, out var editingProject) && editingProject == projectId)
        {
            _editing.TryRemove(connectionId, out editing);
            _editingProject.TryRemove(connectionId, out _);
        }

        var wasLastConnection = true;
        if (_projectUsers.TryGetValue(projectId, out var users) &&
            users.TryGetValue(userId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
            wasLastConnection = connections.IsEmpty;
            if (wasLastConnection)
                users.TryRemove(userId, out _);
        }

        return new PresenceLeaveResult(projectId, userId, wasLastConnection, editing);
    }

    public Guid? SetEditing(string connectionId, Guid? taskId)
    {
        if (!_connectionUsers.ContainsKey(connectionId))
            return null;

        var projectId = ProjectFor(connectionId);
        if (projectId is null)
            return null;

        _editing.TryGetValue(connectionId, out var previous);
        if (taskId is null)
        {
            _editing.TryRemove(connectionId, out _);
            _editingProject.TryRemove(connectionId, out _);
            return previous;
        }

        _editing[connectionId] = taskId;
        _editingProject[connectionId] = projectId.Value;
        return previous;
    }

    public Guid? ProjectFor(string connectionId)
    {
        if (!_focusProject.TryGetValue(connectionId, out var projectId))
            return null;
        if (_connectionProjects.TryGetValue(connectionId, out var projects) && projects.ContainsKey(projectId))
            return projectId;
        return null;
    }

    public IReadOnlyCollection<Guid> OnlineUserIds(Guid projectId) =>
        _projectUsers.TryGetValue(projectId, out var users) ? users.Keys.ToList() : [];

    public IReadOnlyCollection<string> ConnectionIds(Guid projectId, Guid userId)
    {
        if (!_userConnections.TryGetValue(userId, out var connections))
            return [];

        var matches = new List<string>();
        foreach (var connectionId in connections.Keys)
        {
            if (_connectionProjects.TryGetValue(connectionId, out var projects) && projects.ContainsKey(projectId))
                matches.Add(connectionId);
        }

        return matches;
    }
}
