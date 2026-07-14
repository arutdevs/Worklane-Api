using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Worklane.Application.Common.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Identity;

namespace Worklane.Infrastructure.Persistence;

public class WorklaneDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    private readonly ICurrentTenantService _currentTenantService;

    public WorklaneDbContext(DbContextOptions<WorklaneDbContext> options, ICurrentTenantService currentTenantService)
        : base(options)
    {
        _currentTenantService = currentTenantService;
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(WorklaneDbContext).Assembly);

        // Global tenant-isolation guard: once RequireWorkspaceRoleFilter validates membership and
        // calls ICurrentTenantService.SetWorkspace, every workspace-scoped query is pinned to that
        // tenant even if a query forgets an explicit WorkspaceId predicate. When no tenant is set
        // (e.g. "list my workspaces" flows that filter by UserId instead) the filter is a no-op.
        builder.Entity<WorkspaceMember>().HasQueryFilter(m =>
            !_currentTenantService.WorkspaceId.HasValue || m.WorkspaceId == _currentTenantService.WorkspaceId);

        builder.Entity<Project>().HasQueryFilter(p =>
            !p.IsArchived &&
            (!_currentTenantService.WorkspaceId.HasValue || p.WorkspaceId == _currentTenantService.WorkspaceId));

        builder.Entity<ProjectMember>().HasQueryFilter(pm =>
            !_currentTenantService.WorkspaceId.HasValue || pm.Project.WorkspaceId == _currentTenantService.WorkspaceId);

        builder.Entity<Invitation>().HasQueryFilter(i =>
            !_currentTenantService.WorkspaceId.HasValue || i.WorkspaceId == _currentTenantService.WorkspaceId);
    }
}
