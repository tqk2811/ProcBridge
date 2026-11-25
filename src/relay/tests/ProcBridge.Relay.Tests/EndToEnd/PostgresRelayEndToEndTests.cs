using ProcBridge.Relay.Tests.Infrastructure;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>
/// Same scenarios on real PostgreSQL with the real EF repositories and migrations (temporary database, dropped at the
/// end). Skipped (inconclusive) when <see cref="PostgresTestDatabase.EnvironmentVariable"/> is not set.
/// </summary>
[TestClass]
public sealed class PostgresRelayEndToEndTests : RelayEndToEndTestsBase
{
    private static PostgresTestDatabase? _database;
    private static RelayApiFactory? _factory;

    protected override RelayApiFactory Factory
    {
        get
        {
            if (_factory is null)
                Assert.Inconclusive($"Chưa đặt {PostgresTestDatabase.EnvironmentVariable} (chuỗi kết nối PostgreSQL được tạo database).");
            return _factory;
        }
    }

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        if (PostgresTestDatabase.AdminConnectionString is null)
            return;
        _database = PostgresTestDatabase.Create();
        _factory = new RelayApiFactory(_database.ConnectionString);
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (_database is not null)
            await _database.DisposeAsync();
    }
}
