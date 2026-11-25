using Microsoft.EntityFrameworkCore;

namespace ProcBridge.Relay.Database.PostgreSql.EF;

/// <summary>
/// Applies pending migrations. Exactly ONE host may call this (the relay Api); any other host that opens this database
/// must only check <c>GetPendingMigrationsAsync</c> and warn, or two hosts starting together race on
/// <c>__EFMigrationsHistory</c>.
/// </summary>
public static class PostgreSqlSchemaInitializer
{
    public static Task InitializeAsync(RelayDbContext db, CancellationToken cancellationToken = default)
        => db.Database.MigrateAsync(cancellationToken);
}
