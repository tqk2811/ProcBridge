using Npgsql;

namespace ProcBridge.Relay.Tests.Infrastructure;

/// <summary>
/// A throw-away database on a REAL PostgreSQL server. <see cref="EnvironmentVariable"/> holds the connection string of an
/// account allowed to create databases (e.g. <c>Host=localhost;Username=postgres;Password=...</c>); without it the
/// Postgres tests are reported as skipped (inconclusive), never as a fake green.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "PROCBRIDGE_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private readonly string _name;

    private PostgresTestDatabase(string adminConnectionString, string name)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Connection string of the new database. It is created by the relay's own migrations when the host starts.</summary>
    public string ConnectionString { get; }

    public static string? AdminConnectionString
        => Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } value ? value : null;

    public static PostgresTestDatabase Create()
        => new(AdminConnectionString ?? throw new InvalidOperationException($"Thiếu {EnvironmentVariable}."),
            "procbridge_relay_test_" + Guid.NewGuid().ToString("N"));

    public async ValueTask DisposeAsync()
    {
        // Only this database's pool: clearing every pool would cut other test classes running in parallel.
        using (var own = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(own);

        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
