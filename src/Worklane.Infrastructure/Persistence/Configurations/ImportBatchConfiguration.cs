using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Source).HasConversion(ImportConverters.Source).HasMaxLength(16).IsRequired();
        builder.Property(b => b.Status).HasConversion(ImportConverters.BatchStatus).HasMaxLength(32).IsRequired();
        builder.Property(b => b.OriginalFileName).IsRequired().HasMaxLength(200);
        builder.Property(b => b.StorageKey).HasMaxLength(512);
        builder.Property(b => b.FileDeletedAt);
        builder.Property(b => b.ExpiresAt).IsRequired();
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.Property(b => b.HeadersJson).IsRequired().HasColumnType("jsonb");
        builder.Property(b => b.SampleJson).HasColumnType("jsonb");
        builder.Property(b => b.SuggestedMappingJson).HasColumnType("jsonb");
        builder.Property(b => b.MappingJson).HasColumnType("jsonb");
        builder.Property(b => b.WarningsJson).HasColumnType("jsonb");

        builder.HasOne(b => b.Project)
            .WithMany()
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.ProjectId, b.CreatedAt })
            .HasDatabaseName("IX_ImportBatches_ProjectId_CreatedAt");
    }
}
