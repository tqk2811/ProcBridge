using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ProcBridge.Relay.Database.PostgreSql.EF;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>. Reads <c>efdesign.json</c> + <c>efdesign.{Environment}.json</c>
/// (the second file is git-ignored and holds the local password) + environment variables.
/// </summary>
public sealed class RelayDbContextFactory : IDesignTimeDbContextFactory<RelayDbContext>
{
    public RelayDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("efdesign.json", optional: false)
            .AddJsonFile($"efdesign.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("Relay")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Relay trong efdesign.json.");
        return new RelayDbContext(new DbContextOptionsBuilder<RelayDbContext>().UseNpgsql(connectionString).Options);
    }
}
