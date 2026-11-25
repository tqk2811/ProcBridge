using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using ProcBridge.Relay.Database.Entities;

namespace ProcBridge.Relay.Database.PostgreSql.Configurations;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<DeviceDbo>
{
    public void Configure(EntityTypeBuilder<DeviceDbo> builder)
    {
        builder.ToTable("Devices");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.TokenHash).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.OwnerAccountId);
        builder.HasOne(x => x.OwnerAccount).WithMany(x => x.Devices).HasForeignKey(x => x.OwnerAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
