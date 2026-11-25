using Npgsql;

namespace ProcBridge.Relay.Testing.Infrastructure;

/// <summary>
/// A throw-away database on a REAL PostgreSQL server. <see cref="EnvironmentVariable"/> holds the connection string of an
/// account allowed to create databases (e.g. <c>Host=localhost;Username=postgres;Password=...</c>); without it the
/// Postgres tests are reported as skipped (inconclusive), never as a fake green, unless
/// <see cref="RequireEnvironmentVariable"/> is <c>1</c> (CI): then a missing database is a failure, so a CI job that
/// lost its PostgreSQL cannot go green by skipping.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "PROCBRIDGE_TEST_POSTGRES";

    /// <summary>Set to <c>1</c> on CI: Postgres tests fail instead of skipping when <see cref="EnvironmentVariable"/> is missing.</summary>
    public const string RequireEnvironmentVariable = "PROCBRIDGE_REQUIRE_POSTGRES";

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

    /// <summary>True when the missing database must fail the tests (<see cref="RequireEnvironmentVariable"/> is <c>1</c>).</summary>
    public static bool IsRequired => IsRequiredValue(Environment.GetEnvironmentVariable(RequireEnvironmentVariable));

    public static bool IsRequiredValue(string? value) => value is "1";

    /// <summary>Message of the failure when the database is required but <see cref="EnvironmentVariable"/> is not set.</summary>
    public static string MissingMessage => $"{RequireEnvironmentVariable}=1 nhưng chưa đặt {EnvironmentVariable} (chuỗi kết nối PostgreSQL được tạo database).";

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
