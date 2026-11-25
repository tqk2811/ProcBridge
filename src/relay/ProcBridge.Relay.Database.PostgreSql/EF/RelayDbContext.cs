using Microsoft.EntityFrameworkCore;

using ProcBridge.Relay.Database.Entities;

namespace ProcBridge.Relay.Database.PostgreSql.EF;

public sealed class RelayDbContext : DbContext
{
    public RelayDbContext(DbContextOptions<RelayDbContext> options) : base(options)
    {
    }

    public DbSet<AccountDbo> Accounts => Set<AccountDbo>();
    public DbSet<AccountSessionDbo> AccountSessions => Set<AccountSessionDbo>();
    public DbSet<DeviceDbo> Devices => Set<DeviceDbo>();
    public DbSet<ShareDbo> Shares => Set<ShareDbo>();
    /// <summary>Which client device may use which share.</summary>
    public DbSet<ShareGrantDbo> ShareGrants => Set<ShareGrantDbo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayDbContext).Assembly);
    }
}
