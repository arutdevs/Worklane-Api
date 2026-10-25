using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;

namespace Worklane.WebApi.Seed;

/// <summary>
/// Seeds a usable local workspace.
///
/// Each step checks for its own data rather than the whole thing hanging off one
/// "has the demo user been created" test. That earlier shape meant a database seeded
/// before the board existed never got columns or tasks, and the developer opened an
/// empty board with no way to add a column — the seeding code was there, it just
/// never ran again.
/// </summary>
public static class DevSeeder
{
    private const string DemoEmail = "demo@worklane.app";
    private const string DemoPassword = "Demo123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<WorklaneDbContext>();
        var userManager = services.GetRequiredService<UserManager<AppUser>>();

        var demoUser = await EnsureUserAsync(userManager);
        if (demoUser is null) return;

        var workspace = await EnsureWorkspaceAsync(db, demoUser);
        var project = await EnsureProjectAsync(db, workspace, demoUser);
        var columns = await EnsureColumnsAsync(db, project);
        await EnsureTasksAsync(db, project, demoUser, columns);
    }

    private static async Task<AppUser?> EnsureUserAsync(UserManager<AppUser> userManager)
    {
        var existing = await userManager.FindByEmailAsync(DemoEmail);
        if (existing is not null) return existing;

        var user = new AppUser
        {
            UserName = DemoEmail,
            Email = DemoEmail,
            EmailConfirmed = true,
            DisplayName = "Demo User",
            PreferredLanguage = "en"
        };

        var result = await userManager.CreateAsync(user, DemoPassword);
        return result.Succeeded ? user : null;
    }

    private static async Task<Workspace> EnsureWorkspaceAsync(WorklaneDbContext db, AppUser owner)
    {
        var workspace = await db.Workspaces
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Slug == "demo-users-workspace");

        if (workspace is null)
        {
            workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = "Demo User's Workspace",
                Slug = "demo-users-workspace",
                Plan = WorkspacePlan.Free,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
        }

        var isMember = await db.WorkspaceMembers
            .IgnoreQueryFilters()
            .AnyAsync(m => m.WorkspaceId == workspace.Id && m.UserId == owner.Id);

        if (!isMember)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = owner.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        return workspace;
    }

    private static async Task<Project> EnsureProjectAsync(WorklaneDbContext db, Workspace workspace, AppUser owner)
    {
        var project = await db.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspace.Id && p.Key == "DEMO");

        if (project is not null) return project;

        project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "Demo Project",
            Key = "DEMO",
            Description = "A sample project seeded for local development.",
            CreatedById = owner.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            IsArchived = false
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return project;
    }

    /// <summary>
    /// Returns the project's columns by name, adding any of the default three that are
    /// missing. Topping up rather than all-or-nothing matters because a half-built board
    /// is exactly what the old seeder left behind.
    ///
    /// <see cref="BoardColumn.IsInProgress"/> is the flag that starts the clock: without
    /// it nothing ever stamps a start time and every man-day figure stays blank.
    /// </summary>
    private static async Task<Dictionary<string, BoardColumn>> EnsureColumnsAsync(
        WorklaneDbContext db, Project project)
    {
        var byName = await db.BoardColumns
            .IgnoreQueryFilters()
            .Where(c => c.ProjectId == project.Id)
            .ToDictionaryAsync(c => c.Name, c => c);

        var added = false;
        foreach (var columnSeed in WorkspaceLocale.EnglishColumns)
        {
            if (byName.ContainsKey(columnSeed.Name)) continue;

            var column = new BoardColumn
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Name = columnSeed.Name,
                Order = columnSeed.Order,
                IsDone = columnSeed.IsDone,
                IsInProgress = columnSeed.IsInProgress
            };
            db.BoardColumns.Add(column);
            byName[columnSeed.Name] = column;
            added = true;
        }

        if (added) await db.SaveChangesAsync();
        return byName;
    }

    private static async Task EnsureTasksAsync(
        WorklaneDbContext db, Project project, AppUser owner, Dictionary<string, BoardColumn> columns)
    {
        var hasTasks = await db.TaskItems.IgnoreQueryFilters().AnyAsync(t => t.ProjectId == project.Id);
        if (hasTasks) return;

        // Fall back to the first column if the board has been renamed, so seeding still
        // produces something rather than throwing on a missing key.
        BoardColumn Column(string name) =>
            columns.TryGetValue(name, out var c) ? c : columns.Values.OrderBy(x => x.Order).First();

        var todo = Column("To Do");
        var inProgress = Column("In Progress");
        var done = Column("Done");

        var now = DateTimeOffset.UtcNow;
        var tasks = new[]
        {
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = todo.Id,
                Number = 1, Title = "Set up project structure",
                Description = "Initialize repo, configure CI/CD pipeline, and set up shared tooling.",
                Priority = TaskPriority.High, EstimateDays = 2, Order = 65536, CreatedById = owner.Id,
                CreatedAt = now.AddDays(-5), UpdatedAt = now.AddDays(-5)
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = todo.Id,
                Number = 2, Title = "Define data model",
                Description = "Draft ERD for all domain entities and confirm relationships with the team.",
                Priority = TaskPriority.Medium, EstimateDays = 1, Order = 131072, CreatedById = owner.Id,
                CreatedAt = now.AddDays(-4), UpdatedAt = now.AddDays(-4),
                DueDate = now.AddDays(3)
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = inProgress.Id,
                Number = 3, Title = "Implement authentication",
                Description = "JWT-based login, registration, token refresh, and logout endpoints.",
                Priority = TaskPriority.Urgent, EstimateDays = 3, Order = 65536, CreatedById = owner.Id,
                AssigneeId = owner.Id,
                StartedAt = now.AddDays(-3),
                CreatedAt = now.AddDays(-3), UpdatedAt = now.AddDays(-1)
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = inProgress.Id,
                Number = 4, Title = "Build workspace management",
                Description = "Create, invite members, assign roles, and accept invitations.",
                // Started a while ago against a small estimate, so the board shows what an
                // overrun looks like without anyone having to wait for one.
                Priority = TaskPriority.High, EstimateDays = 1, Order = 131072, CreatedById = owner.Id,
                AssigneeId = owner.Id,
                StartedAt = now.AddDays(-9),
                CreatedAt = now.AddDays(-2), UpdatedAt = now,
                DueDate = now.AddDays(-1)   // overdue — for UI testing
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = done.Id,
                Number = 5, Title = "Configure PostgreSQL + EF Core",
                Description = "Set up DbContext, run initial migration, wire up Identity tables.",
                Priority = TaskPriority.Medium, EstimateDays = 2, Order = 65536, CreatedById = owner.Id,
                StartedAt = now.AddDays(-6), CompletedAt = now.AddDays(-5),
                CreatedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-5)
            },
        };

        db.TaskItems.AddRange(tasks);

        var hasCounter = await db.ProjectTaskCounters.IgnoreQueryFilters().AnyAsync(c => c.ProjectId == project.Id);
        if (!hasCounter)
            db.ProjectTaskCounters.Add(new ProjectTaskCounter { ProjectId = project.Id, LastNumber = tasks.Length });

        await db.SaveChangesAsync();
    }
}
