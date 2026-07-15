using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.WebApi.Seed;

public static class DevSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<WorklaneDbContext>();
        var userManager = services.GetRequiredService<UserManager<AppUser>>();

        // Skip if already seeded
        if (await userManager.FindByEmailAsync("demo@worklane.app") is not null)
            return;

        // ── User ────────────────────────────────────────────────────────────────
        var demoUser = new AppUser
        {
            UserName = "demo@worklane.app",
            Email = "demo@worklane.app",
            EmailConfirmed = true,
            DisplayName = "Demo User",
            PreferredLanguage = "en"
        };

        var createResult = await userManager.CreateAsync(demoUser, "Demo123!");
        if (!createResult.Succeeded) return;

        // ── Workspace ────────────────────────────────────────────────────────────
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Demo User's Workspace",
            Slug = "demo-users-workspace",
            Plan = WorkspacePlan.Free,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Workspaces.Add(workspace);

        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = demoUser.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow
        });

        // ── Project ──────────────────────────────────────────────────────────────
        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "Demo Project",
            Key = "DEMO",
            Description = "A sample project seeded for local development.",
            CreatedById = demoUser.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            IsArchived = false
        };
        db.Projects.Add(project);

        // ── Default columns ───────────────────────────────────────────────────────
        var todoCol = new BoardColumn { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "To Do", Order = 1000, IsDone = false };
        var inProgressCol = new BoardColumn { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "In Progress", Order = 2000, IsDone = false };
        var doneCol = new BoardColumn { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Done", Order = 3000, IsDone = true };

        db.BoardColumns.AddRange(todoCol, inProgressCol, doneCol);

        // ── Task counter ──────────────────────────────────────────────────────────
        db.ProjectTaskCounters.Add(new ProjectTaskCounter { ProjectId = project.Id, LastNumber = 5 });

        // ── Demo tasks ────────────────────────────────────────────────────────────
        var now = DateTimeOffset.UtcNow;
        var tasks = new[]
        {
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = todoCol.Id,
                Number = 1, Title = "Set up project structure",
                Description = "Initialize repo, configure CI/CD pipeline, and set up shared tooling.",
                Priority = TaskPriority.High, Order = 65536, CreatedById = demoUser.Id,
                CreatedAt = now.AddDays(-5), UpdatedAt = now.AddDays(-5)
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = todoCol.Id,
                Number = 2, Title = "Define data model",
                Description = "Draft ERD for all domain entities and confirm relationships with the team.",
                Priority = TaskPriority.Medium, Order = 131072, CreatedById = demoUser.Id,
                CreatedAt = now.AddDays(-4), UpdatedAt = now.AddDays(-4),
                DueDate = now.AddDays(3)
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = inProgressCol.Id,
                Number = 3, Title = "Implement authentication",
                Description = "JWT-based login, registration, token refresh, and logout endpoints.",
                Priority = TaskPriority.Urgent, Order = 65536, CreatedById = demoUser.Id,
                AssigneeId = demoUser.Id,
                CreatedAt = now.AddDays(-3), UpdatedAt = now.AddDays(-1)
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = inProgressCol.Id,
                Number = 4, Title = "Build workspace management",
                Description = "Create, invite members, assign roles, and accept invitations.",
                Priority = TaskPriority.High, Order = 131072, CreatedById = demoUser.Id,
                AssigneeId = demoUser.Id,
                CreatedAt = now.AddDays(-2), UpdatedAt = now,
                DueDate = now.AddDays(-1)   // overdue — for UI testing
            },
            new TaskItem
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ColumnId = doneCol.Id,
                Number = 5, Title = "Configure PostgreSQL + EF Core",
                Description = "Set up DbContext, run initial migration, wire up Identity tables.",
                Priority = TaskPriority.Medium, Order = 65536, CreatedById = demoUser.Id,
                CreatedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-5)
            },
        };

        db.TaskItems.AddRange(tasks);

        await db.SaveChangesAsync();
    }
}
