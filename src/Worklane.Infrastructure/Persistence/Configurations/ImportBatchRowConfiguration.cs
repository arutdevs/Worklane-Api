using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class ImportBatchRowConfiguration : IEntityTypeConfiguration<ImportBatchRow>
{
    public void Configure(EntityTypeBuilder<ImportBatchRow> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.RowNumber).IsRequired();
        builder.Property(r => r.ExternalId).HasMaxLength(200);
        builder.Property(r => r.ParentExternalId).HasMaxLength(200);
        builder.Property(r => r.Title).HasMaxLength(500);
        builder.Property(r => r.Description).HasMaxLength(10_000);
        builder.Property(r => r.ColumnName).HasMaxLength(100);
        builder.Property(r => r.AssigneeEmail).HasMaxLength(320);
        builder.Property(r => r.PriorityText).HasMaxLength(50);
        builder.Property(r => r.DueDateText).HasMaxLength(80);
        builder.Property(r => r.EstimateText).HasMaxLength(40);
        builder.Property(r => r.Status).HasConversion(ImportConverters.RowStatus).HasMaxLength(32).IsRequired();
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.WarningCode).HasMaxLength(64);

        builder.HasOne(r => r.ImportBatch)
            .WithMany(b => b.Rows)
            .HasForeignKey(r => r.ImportBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Task)
            .WithMany()
            .HasForeignKey(r => r.TaskId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasIndex(r => new { r.ImportBatchId, r.RowNumber })
            .IsUnique()
            .HasDatabaseName("IX_ImportBatchRows_ImportBatchId_RowNumber");
    }
}
