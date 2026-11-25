using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using ProcBridge.Relay.Database.Entities;

namespace ProcBridge.Relay.Database.PostgreSql.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<AccountDbo>
{
    public void Configure(EntityTypeBuilder<AccountDbo> builder)
    {
        builder.ToTable("Accounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserName).IsRequired();
        builder.Property(x => x.PasswordHash).IsRequired();
        builder.HasIndex(x => x.UserName).IsUnique();
    }
}
