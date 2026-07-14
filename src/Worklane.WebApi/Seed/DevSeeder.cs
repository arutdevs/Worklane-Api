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

        var demoUser = await userManager.FindByEmailAsync("demo@worklane.app");
        if (demoUser is not null)
        {
            return;
        }

        demoUser = new AppUser
        {
            UserName = "demo@worklane.app",
            Email = "demo@worklane.app",
            EmailConfirmed = true,
            DisplayName = "Demo User",
            PreferredLanguage = "en"
        };

        var createResult = await userManager.CreateAsync(demoUser, "Demo123!");
        if (!createResult.Succeeded)
        {
            return;
        }

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

        db.Projects.Add(new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "Demo Project",
            Key = "DEMO",
            Description = "A sample project seeded for local development.",
            CreatedById = demoUser.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            IsArchived = false
        });

        await db.SaveChangesAsync();
    }
}
