using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using ProcBridge.Relay.Database.Entities;

namespace ProcBridge.Relay.Database.PostgreSql.Configurations;

public sealed class ShareConfiguration : IEntityTypeConfiguration<ShareDbo>
{
    public void Configure(EntityTypeBuilder<ShareDbo> builder)
    {
        builder.ToTable("Shares");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired();
        builder.HasIndex(x => x.DeviceId);
        builder.HasOne(x => x.Device).WithMany(x => x.Shares).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
    }
}
