using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class ProjectTaskCounterConfiguration : IEntityTypeConfiguration<ProjectTaskCounter>
{
    public void Configure(EntityTypeBuilder<ProjectTaskCounter> builder)
    {
        builder.HasKey(c => c.ProjectId);

        builder.Property(c => c.LastNumber).IsRequired().HasDefaultValue(0);

        builder.HasOne(c => c.Project)
            .WithOne()
            .HasForeignKey<ProjectTaskCounter>(c => c.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
