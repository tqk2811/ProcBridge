using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using ProcBridge.Relay.Database.Entities;

namespace ProcBridge.Relay.Database.PostgreSql.Configurations;

public sealed class AccountSessionConfiguration : IEntityTypeConfiguration<AccountSessionDbo>
{
    public void Configure(EntityTypeBuilder<AccountSessionDbo> builder)
    {
        builder.ToTable("AccountSessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TokenHash).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.AccountId);
        builder.HasOne(x => x.Account).WithMany(x => x.Sessions).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
