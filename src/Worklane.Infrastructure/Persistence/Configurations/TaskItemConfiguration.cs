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
            .HasDefaultValue(TaskPriority.Medium)
            // TaskPriority.Low is 0 — the CLR default — which EF otherwise reads as
            // "never set" and drops from the INSERT, letting the column default (Medium)
            // overwrite it. Move the sentinel to a value nothing can assign so an
            // explicit Low is always written.
            .HasSentinel((TaskPriority)(-1));
        builder.Property(t => t.Order).IsRequired();
        builder.Property(t => t.EstimateDays);
        builder.Property(t => t.StartedAt);
        builder.Property(t => t.CompletedAt);
        builder.Property(t => t.IsArchived).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.ArchivedAt);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
        builder.Property(t => t.ImportSource)
            .HasConversion(ImportConverters.NullableSource)
            .HasMaxLength(16);
        builder.Property(t => t.ExternalId).HasMaxLength(200);

        builder.HasOne(t => t.Project)
            .WithMany()
            .HasForeignKey(t => t.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Column)
            .WithMany(c => c.Tasks)
            .HasForeignKey(t => t.ColumnId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exists so the self-FK can target (ProjectId, Id). Id is already the primary key;
        // a duplicate here is not a user-facing conflict.
        builder.HasAlternateKey(t => new { t.ProjectId, t.Id })
            .HasName("AK_TaskItems_ProjectId_Id");

        // Same project is required: (ProjectId, ParentTaskId) matches a row in this project.
        // RESTRICT so a parent cannot disappear while a child still points at it.
        // Project cascade deletes tasks; a hard delete of a project that still has links
        // fails until those links are cleared. No project hard-delete path exists.
        builder.HasOne(t => t.Parent)
            .WithMany(t => t.Children)
            .HasForeignKey(t => new { t.ProjectId, t.ParentTaskId })
            .HasPrincipalKey(t => new { t.ProjectId, t.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false)
            .HasConstraintName("FK_TaskItems_TaskItems_ProjectId_ParentTaskId");

        builder.HasIndex(t => new { t.ProjectId, t.ParentTaskId })
            .HasDatabaseName("IX_TaskItems_ProjectId_ParentTaskId")
            .HasFilter("\"ParentTaskId\" IS NOT NULL");

        builder.HasOne(t => t.ImportBatch)
            .WithMany(b => b.Tasks)
            .HasForeignKey(t => t.ImportBatchId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasIndex(t => new { t.ProjectId, t.ImportSource, t.ExternalId })
            .IsUnique()
            .HasFilter("\"ExternalId\" IS NOT NULL")
            .HasDatabaseName("IX_TaskItems_ProjectId_ImportSource_ExternalId");

        builder.HasIndex(t => t.ImportBatchId)
            .HasFilter("\"ImportBatchId\" IS NOT NULL")
            .HasDatabaseName("IX_TaskItems_ImportBatchId");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_TaskItems_NotSelfParent",
                "\"ParentTaskId\" IS NULL OR \"ParentTaskId\" <> \"Id\"");
            t.HasCheckConstraint(
                "CK_TaskItems_ExternalIdRequiresSource",
                "\"ExternalId\" IS NULL OR \"ImportSource\" IS NOT NULL");
        });

        // Unique per-project task number (display key generation)
        builder.HasIndex(t => new { t.ProjectId, t.Number }).IsUnique();

        // Ordering queries
        builder.HasIndex(t => new { t.ColumnId, t.Order });
    }
}
