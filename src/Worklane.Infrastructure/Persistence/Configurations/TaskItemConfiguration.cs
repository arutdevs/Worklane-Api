using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Title).IsRequired().HasMaxLength(500);
        builder.Property(t => t.Description).HasMaxLength(10_000);
        builder.Property(t => t.Priority)
            .IsRequired()
            .HasConversion<string>()
            .HasDefaultValue(TaskPriority.Medium);
        builder.Property(t => t.Order).IsRequired();
        builder.Property(t => t.IsArchived).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        builder.HasOne(t => t.Project)
            .WithMany()
            .HasForeignKey(t => t.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Column)
            .WithMany(c => c.Tasks)
            .HasForeignKey(t => t.ColumnId)
            .OnDelete(DeleteBehavior.Restrict);

        // Unique per-project task number (display key generation)
        builder.HasIndex(t => new { t.ProjectId, t.Number }).IsUnique();

        // Ordering queries
        builder.HasIndex(t => new { t.ColumnId, t.Order });
    }
}
