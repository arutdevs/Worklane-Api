using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class ProjectExportLogConfiguration : IEntityTypeConfiguration<ProjectExportLog>
{
    public void Configure(EntityTypeBuilder<ProjectExportLog> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Format).HasConversion(ImportConverters.Format).HasMaxLength(8).IsRequired();
        builder.Property(l => l.RowCount).IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();

        builder.HasOne(l => l.Project)
            .WithMany()
            .HasForeignKey(l => l.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.ProjectId, l.CreatedAt })
            .HasDatabaseName("IX_ProjectExportLogs_ProjectId_CreatedAt");
    }
}
