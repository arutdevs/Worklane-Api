using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Worklane.Domain.Entities;

namespace Worklane.Infrastructure.Persistence.Configurations;

public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Email).IsRequired().HasMaxLength(320);
        builder.Property(i => i.Token).IsRequired().HasMaxLength(128);
        builder.HasIndex(i => i.Token).IsUnique();

        builder.HasOne(i => i.Workspace)
            .WithMany(w => w.Invitations)
            .HasForeignKey(i => i.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
