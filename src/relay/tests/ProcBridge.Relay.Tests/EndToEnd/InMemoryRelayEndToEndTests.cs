using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>End-to-end scenarios on in-memory repositories: runs everywhere, CI included.</summary>
[TestClass]
public sealed class InMemoryRelayEndToEndTests : RelayEndToEndTestsBase
{
    private static RelayApiFactory? _factory;

    protected override RelayApiFactory Factory => _factory!;

    [ClassInitialize]
    public static void Initialize(TestContext context) => _factory = new RelayApiFactory();

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }
}
