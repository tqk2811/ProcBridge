using System.Diagnostics;
using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>Broker cache invalidated by the Agent's change notifications, and the start-up tree preload.</summary>
[TestClass]
public sealed class BrokerWatchTests
{
    private static readonly TransportOptions Transport = new()
    {
        ConnectionCount = 2,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>TTLs long enough that only a notification can make the cache ask machine B again.</summary>
    private static readonly FileCacheOptions LongTtl = new() { MetadataTtl = TimeSpan.FromMinutes(5), WatchedMetadataTtl = TimeSpan.FromMinutes(5) };

    private static AgentServer StartAgent(TempFolder share)
    {
        var server = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        return server;
    }

    private static AgentClient CreateClient(int port) => new(new TcpConnectionFactory("127.0.0.1", port, Transport), "src", Transport);

    private static long Requests(AgentClient client) => client.Pool.GetRequestsPerSlot().Sum();

    /// <summary>Polls <paramref name="condition"/> until it holds; returns the time it took or fails after <paramref name="timeout"/>.</summary>
    private static async Task<TimeSpan> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
                Assert.Fail($"Condition not met within {timeout.TotalMilliseconds} ms.");
            await Task.Delay(10);
        }
        return watch.Elapsed;
    }

    [TestMethod]
    public async Task ChangeOnAgent_InvalidatesStatListAndContent_WithinOneSecond()
    {
        using var share = new TempFolder("bwatch");
        string file = share.WriteFile(@"proj\a.txt", "1"u8.ToArray());
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, LongTtl);
        await using ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(200));
        cache.AttachWatcher(watcher);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(cache.IsWatchActive);

        Assert.AreEqual(1L, (await cache.StatAsync("proj/a.txt", CancellationToken.None)).Size);
        Assert.HasCount(1, await cache.ListAsync("proj", CancellationToken.None));
        CachedItem first = await cache.EnsureAsync("proj/a.txt", CancellationToken.None);
        Assert.AreEqual("1", File.ReadAllText(first.LocalPath));

        File.WriteAllText(file, "12345");
        TimeSpan statDelay = await WaitUntilAsync(async () => (await cache.StatAsync("proj/a.txt", CancellationToken.None)).Size == 5, TimeSpan.FromSeconds(1));
        CachedItem second = await cache.EnsureAsync("proj/a.txt", CancellationToken.None);
        Assert.AreEqual("12345", File.ReadAllText(second.LocalPath));

        share.WriteFile(@"proj\b.txt", [1]);
        await WaitUntilAsync(async () => (await cache.ListAsync("proj", CancellationToken.None)).Count == 2, TimeSpan.FromSeconds(1));

        Directory.Delete(share.Combine("proj"), recursive: true);
        await WaitUntilAsync(async () =>
        {
            try
            {
                await cache.StatAsync("proj/a.txt", CancellationToken.None);
                return false;
            }
            catch (ProcBridge.Protocol.ProtocolStatusException)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(1));
        Console.WriteLine($"stat invalidated after {statDelay.TotalMilliseconds:0} ms");
    }

    [TestMethod]
    public async Task UnchangedFile_WhileWatching_ServedFromCacheWithoutRequests()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile("a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        // Short TTL: without the watcher every stat after 50 ms would go to machine B.
        var cache = new FileCache(client, cacheDir.Path, new FileCacheOptions { MetadataTtl = TimeSpan.FromMilliseconds(50), WatchedMetadataTtl = TimeSpan.FromMinutes(5) });
        await using ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(200));
        cache.AttachWatcher(watcher);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await cache.StatAsync("a.txt", CancellationToken.None);
        long before = Requests(client);
        await Task.Delay(200);
        await cache.StatAsync("a.txt", CancellationToken.None);
        Assert.AreEqual(before, Requests(client));
    }

    [TestMethod]
    public async Task Rescan_ClearsMetadata()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile("a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, LongTtl);
        await using ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(200));
        cache.AttachWatcher(watcher);
        var rescans = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        cache.RemoteChanged += x =>
        {
            if (x.Rescan)
                rescans.TrySetResult();
        };
        await cache.StatAsync("a.txt", CancellationToken.None);
        long before = Requests(client);

        server.GetWatcher(new ReadOnlyShare("src", share.Path)).OnError(new InternalBufferOverflowException());

        await rescans.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cache.StatAsync("a.txt", CancellationToken.None);
        Assert.AreEqual(before + 1, Requests(client));
    }

    [TestMethod]
    public async Task AgentGone_WatcherInactive_FallsBackToTtl_AndResubscribesOnReturn()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile("a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        AgentServer server = StartAgent(share);
        int port = server.Port;
        await using AgentClient client = CreateClient(port);
        var cache = new FileCache(client, cacheDir.Path, new FileCacheOptions { MetadataTtl = TimeSpan.FromMilliseconds(100), WatchedMetadataTtl = TimeSpan.FromMinutes(5) });
        await using ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(100));
        cache.AttachWatcher(watcher);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await server.DisposeAsync();
        await WaitUntilAsync(() => Task.FromResult(!cache.IsWatchActive), TimeSpan.FromSeconds(2));

        await using var restarted = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = port });
        restarted.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(cache.IsWatchActive);
        Assert.AreEqual(2, watcher.SubscribeCount);

        await cache.StatAsync("a.txt", CancellationToken.None);
        File.WriteAllBytes(share.Combine("a.txt"), [1, 2, 3]);
        await WaitUntilAsync(async () => (await cache.StatAsync("a.txt", CancellationToken.None)).Size == 3, TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task PreloadTree_FillsStatsAndListings_NoFurtherRequests()
    {
        using var share = new TempFolder("bwatch");
        for (int d = 0; d < 4; d++)
        {
            for (int f = 0; f < 20; f++)
                share.WriteFile($@"src\dir{d}\f{f}.cs", new byte[f]);
            Directory.CreateDirectory(share.Combine($@"src\dir{d}\empty"));
        }
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, LongTtl);

        int loaded = await cache.PreloadTreeAsync("src", 0, CancellationToken.None);
        Assert.AreEqual(4 + 4 * 21, loaded);

        long before = Requests(client);
        for (int d = 0; d < 4; d++)
        {
            IReadOnlyList<FsEntry> list = await cache.ListAsync($"src/dir{d}", CancellationToken.None);
            Assert.HasCount(21, list);
            Assert.AreEqual(7L, (await cache.StatAsync($"src/dir{d}/f7.cs", CancellationToken.None)).Size);
            Assert.IsEmpty(await cache.ListAsync($"src/dir{d}/empty", CancellationToken.None));
        }
        Assert.AreEqual(before, Requests(client));
    }

    [TestMethod]
    public async Task BrokerHost_Start_PreloadsMappedTreeAndWatches()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile(@"proj\a.cs", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using BrokerHost host = BrokerHost.Start(new BrokerOptions
        {
            SessionId = ProcBridge.HookIpc.HookIpcProtocol.NewSessionId(),
            AgentHost = "127.0.0.1",
            AgentPort = server.Port,
            ShareName = "src",
            Mappings = [new VirtualMapping(@"X:\virtual\proj", "proj")],
            CacheRoot = cacheDir.Path,
            Transport = Transport,
        });

        await host.PreloadTask.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Watcher!.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        long before = Requests(host.Client);
        Assert.AreEqual(1L, (await host.Cache.StatAsync("proj/a.cs", CancellationToken.None)).Size);
        Assert.AreEqual(before, Requests(host.Client));
    }
}
