using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Key).IsRequired().HasMaxLength(10);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.HasIndex(p => new { p.WorkspaceId, p.Key }).IsUnique();

        builder.HasOne(p => p.Workspace)
            .WithMany(w => w.Projects)
            .HasForeignKey(p => p.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant isolation + soft-delete filters are applied centrally in WorklaneDbContext.OnModelCreating
        // (combined into one predicate per entity, since EF Core only keeps the last HasQueryFilter call).
    }
}
