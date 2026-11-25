using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using ProcBridge.Relay.Database.Entities;

namespace ProcBridge.Relay.Database.PostgreSql.Configurations;

public sealed class ShareGrantConfiguration : IEntityTypeConfiguration<ShareGrantDbo>
{
    public void Configure(EntityTypeBuilder<ShareGrantDbo> builder)
    {
        builder.ToTable("ShareGrants");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.ShareId, x.GranteeDeviceId }).IsUnique();
        builder.HasIndex(x => x.GranteeDeviceId);
        // Deleting a share removes its grants; a device is never hard-deleted (revoked instead), so Restrict there.
        builder.HasOne(x => x.Share).WithMany(x => x.Grants).HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.GranteeDevice).WithMany(x => x.Grants).HasForeignKey(x => x.GranteeDeviceId).OnDelete(DeleteBehavior.Restrict);
    }
}
