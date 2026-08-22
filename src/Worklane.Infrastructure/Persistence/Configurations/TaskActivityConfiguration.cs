using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class TaskActivityConfiguration : IEntityTypeConfiguration<TaskActivity>
{
    public void Configure(EntityTypeBuilder<TaskActivity> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ToColumnId).IsRequired();
        builder.Property(a => a.MovedByUserId).IsRequired();
        builder.Property(a => a.MovedAt).IsRequired();

        builder.HasOne(a => a.Task)
            .WithMany()
            .HasForeignKey(a => a.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reports read a task's history in order; the board reads the newest entries first.
        builder.HasIndex(a => new { a.TaskId, a.MovedAt });
    }
}
