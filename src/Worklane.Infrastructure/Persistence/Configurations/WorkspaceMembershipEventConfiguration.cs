using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class WorkspaceMembershipEventConfiguration : IEntityTypeConfiguration<WorkspaceMembershipEvent>
{
    public void Configure(EntityTypeBuilder<WorkspaceMembershipEvent> builder)
    {
        builder.HasKey(e => e.Id);

        // No HasDefaultValue: WorkspaceMembershipAction.RoleChanged and WorkspaceRole.Viewer
        // are 0, and a default would make EF omit an explicit value from the INSERT.
        builder.Property(e => e.Action)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.OldRole)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(e => e.NewRole)
            .HasMaxLength(16)
            .HasConversion(
                role => role.HasValue ? role.Value.ToString() : null,
                value => string.IsNullOrEmpty(value) ? null : Enum.Parse<WorkspaceRole>(value));

        builder.Property(e => e.ActorUserId).IsRequired();
        builder.Property(e => e.TargetUserId).IsRequired();
        builder.Property(e => e.OccurredAt).IsRequired();

        builder.HasOne(e => e.Workspace)
            .WithMany()
            .HasForeignKey(e => e.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.WorkspaceId, e.OccurredAt });
    }
}
