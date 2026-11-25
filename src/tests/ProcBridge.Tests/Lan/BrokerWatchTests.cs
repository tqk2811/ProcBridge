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
        var server = new AgentServer([new AgentShare("src", share.Path)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
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
        TimeSpan statDelay = await WaitUntilAsync(async () => (await cache.StatAsync("proj/a.txt", CancellationToken.None)).Size == 5, TimeSpan.FromSeconds(5));
        CachedItem second = await cache.EnsureAsync("proj/a.txt", CancellationToken.None);
        Assert.AreEqual("12345", File.ReadAllText(second.LocalPath));

        share.WriteFile(@"proj\b.txt", [1]);
        await WaitUntilAsync(async () => (await cache.ListAsync("proj", CancellationToken.None)).Count == 2, TimeSpan.FromSeconds(5));

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
        }, TimeSpan.FromSeconds(5));
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

        server.GetWatcher(new AgentShare("src", share.Path)).OnError(new InternalBufferOverflowException());

        await rescans.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

        await using var restarted = new AgentServer([new AgentShare("src", share.Path)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = port });
        restarted.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(cache.IsWatchActive);
        Assert.AreEqual(2, watcher.SubscribeCount);

        await cache.StatAsync("a.txt", CancellationToken.None);
        File.WriteAllBytes(share.Combine("a.txt"), [1, 2, 3]);
        await WaitUntilAsync(async () => (await cache.StatAsync("a.txt", CancellationToken.None)).Size == 3, TimeSpan.FromSeconds(5));
    }

    private static async Task<(FileCache Cache, ChangeWatcher Watcher)> WatchedCacheAsync(AgentClient client, string cacheDir)
    {
        var cache = new FileCache(client, cacheDir, LongTtl);
        ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(200));
        cache.AttachWatcher(watcher);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return (cache, watcher);
    }

    [TestMethod]
    public async Task InvalidationDuringStatFetch_DoesNotLeaveTheStaleResultCached()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile("a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        (FileCache cache, ChangeWatcher watcher) = await WatchedCacheAsync(client, cacheDir.Path);
        await using ChangeWatcher _ = watcher;
        int calls = 0;
        // The first fetch is invalidated after its expiry was computed and before it is stored.
        cache.BeforeMetadataStore = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                cache.ClearMetadata();
        };

        await cache.StatAsync("a.txt", CancellationToken.None);
        long before = Requests(client);
        await cache.StatAsync("a.txt", CancellationToken.None);

        Assert.AreEqual(before + 1, Requests(client), "The result of a fetch that raced an invalidation must not be served from the cache.");
    }

    [TestMethod]
    public async Task InvalidationDuringListFetch_DoesNotLeaveTheStaleListingCached()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile(@"d\a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        (FileCache cache, ChangeWatcher watcher) = await WatchedCacheAsync(client, cacheDir.Path);
        await using ChangeWatcher _ = watcher;
        int calls = 0;
        cache.BeforeMetadataStore = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                cache.ClearMetadata();
        };

        await cache.ListAsync("d", CancellationToken.None);
        long before = Requests(client);
        await cache.ListAsync("d", CancellationToken.None);
        Assert.AreEqual(before + 1, Requests(client), "The listing must be fetched again.");
        await cache.StatAsync("d/a.txt", CancellationToken.None);
        Assert.AreEqual(before + 1, Requests(client), "The second listing was not raced, so it fills the child's stat.");
    }

    [TestMethod]
    public async Task InvalidationDuringPreload_DoesNotLeaveTheWalkCached()
    {
        using var share = new TempFolder("bwatch");
        for (int f = 0; f < 5; f++)
            share.WriteFile($@"src\f{f}.cs", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        (FileCache cache, ChangeWatcher watcher) = await WatchedCacheAsync(client, cacheDir.Path);
        await using ChangeWatcher _ = watcher;
        cache.BeforeMetadataStore = cache.ClearMetadata;

        await cache.PreloadTreeAsync("src", 0, CancellationToken.None);
        cache.BeforeMetadataStore = null;

        long before = Requests(client);
        await cache.StatAsync("src/f1.cs", CancellationToken.None);
        await cache.ListAsync("src", CancellationToken.None);
        Assert.AreEqual(before + 2, Requests(client), "Nothing of a walk that raced an invalidation may be cached.");
    }

    [TestMethod]
    public async Task StatAfterInvalidation_DoesNotJoinTheFetchStartedBefore()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile("a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        (FileCache cache, ChangeWatcher watcher) = await WatchedCacheAsync(client, cacheDir.Path);
        await using ChangeWatcher _ = watcher;
        using var held = new ManualResetEventSlim();
        using var reached = new ManualResetEventSlim();
        int calls = 0;
        cache.BeforeMetadataStore = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                reached.Set();
                held.Wait(TimeSpan.FromSeconds(10));
            }
        };

        Task<FsEntry> first = cache.StatAsync("a.txt", CancellationToken.None);
        Assert.IsTrue(reached.Wait(TimeSpan.FromSeconds(5)));
        cache.ClearMetadata();
        Task<FsEntry> second = cache.StatAsync("a.txt", CancellationToken.None);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        held.Set();
        await first;

        Assert.AreEqual(2, Volatile.Read(ref calls), "A stat requested after an invalidation needs a fetch of its own.");
    }

    [TestMethod]
    public async Task HookWatches_DoNotBlockTheThreadThatRaisesTheChange()
    {
        using var share = new TempFolder("bwatch");
        Directory.CreateDirectory(share.Combine("proj"));
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        (FileCache cache, ChangeWatcher watcher) = await WatchedCacheAsync(client, cacheDir.Path);
        await using ChangeWatcher _ = watcher;
        using var registry = new HookWatchRegistry(cache);
        uint id = registry.Add(1, "proj", subtree: true);
        using var held = new ManualResetEventSlim();
        registry.BeforeDispatch = () => held.Wait(TimeSpan.FromSeconds(10));
        // Subscribed after the registry: with a synchronous registry it would only run once the registry was done.
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.RemoteChanged += x =>
        {
            if (x.Changes.Length > 0)
                raised.TrySetResult();
        };

        share.WriteFile(@"proj\a.txt", [1]);
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10));
        held.Set();
        var reply = await registry.WaitAsync(1, id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.IsTrue(reply.Changes.Any(x => x.Name == "a.txt"));
    }

    [TestMethod]
    public async Task WaitAny_BusyWatch_DoesNotKeepOtherWatchesOut()
    {
        using var share = new TempFolder("bwatch");
        Directory.CreateDirectory(share.Combine(@"proj\a"));
        Directory.CreateDirectory(share.Combine(@"proj\b"));
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, LongTtl);
        await using ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(200));
        cache.AttachWatcher(watcher);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300); // the subscription's rescan must not overflow the watches below
        // Room for about two changes per reply.
        using var registry = new HookWatchRegistry(cache, replyBudget: 60);
        registry.Attach(1);
        uint busy = registry.Add(1, "proj/a", subtree: false);
        uint quiet = registry.Add(1, "proj/b", subtree: false);
        int before = registry.DispatchedCount;

        for (int i = 0; i < 30; i++)
            share.WriteFile($@"proj\a\f{i:00}.txt", [1]);
        share.WriteFile(@"proj\b\only.txt", [1]);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && registry.DispatchedCount == before)
            await Task.Delay(20);
        await Task.Delay(500); // let the remaining notifications land

        var carried = new List<uint>();
        for (int poll = 0; poll < 3; poll++)
            carried.AddRange((await registry.WaitAnyAsync(1, TimeSpan.FromSeconds(1), CancellationToken.None)).Watches.Select(x => x.WatchId));
        Assert.Contains(busy, carried);
        Assert.Contains(quiet, carried, "The quiet watch waited behind the busy one.");
    }

    [TestMethod]
    public async Task WaitAny_FromOwnerWithoutConnection_LeavesNoSignalBehind()
    {
        using var share = new TempFolder("bwatch");
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, LongTtl);
        using var registry = new HookWatchRegistry(cache);

        await registry.WaitAnyAsync(42, TimeSpan.Zero, CancellationToken.None);
        Assert.AreEqual(0, registry.SignalCount);

        registry.Attach(7);
        await registry.WaitAnyAsync(7, TimeSpan.Zero, CancellationToken.None);
        Assert.AreEqual(1, registry.SignalCount);
        registry.Detach(7);
        Assert.AreEqual(0, registry.SignalCount);
    }

    [TestMethod]
    public async Task HookWatch_ChangeTooBigForOneReply_BecomesOverflow()
    {
        using var share = new TempFolder("bwatch");
        Directory.CreateDirectory(share.Combine("proj"));
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, LongTtl);
        await using ChangeWatcher watcher = client.CreateWatcher(TimeSpan.FromMilliseconds(200));
        cache.AttachWatcher(watcher);
        watcher.Start();
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        // A budget no change fits: the first queued change can never be delivered.
        var registry = new HookWatchRegistry(cache, replyBudget: 4);
        uint id = registry.Add(1, "proj", subtree: true);

        share.WriteFile(@"proj\a.txt", [1]);
        var reply = await registry.WaitAsync(1, id, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.IsTrue(reply.Overflow);
        Assert.IsEmpty(reply.Changes);
    }

    [TestMethod]
    public async Task AgentWatcherLost_BrokerFallsBackToTtl_AndRecoversWhenWatchingWorksAgain()
    {
        using var share = new TempFolder("bwatch");
        share.WriteFile("a.txt", [1]);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        (FileCache cache, ChangeWatcher watcher) = await WatchedCacheAsync(client, cacheDir.Path);
        await using ChangeWatcher _ = watcher;
        Assert.IsTrue(cache.IsWatchActive);

        // The share root vanishes and the Agent's watcher dies: the Broker must stop trusting its cache.
        string root = share.Path;
        string moved = root + "-moved";
        var readOnlyShare = new AgentShare("src", root);
        Directory.Move(root, moved);
        server.GetWatcher(readOnlyShare).OnError(new IOException("The watcher stopped."));
        await WaitUntilAsync(() => Task.FromResult(!cache.IsWatchActive), TimeSpan.FromSeconds(3));

        Directory.Move(moved, root);
        await watcher.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => Task.FromResult(cache.IsWatchActive), TimeSpan.FromSeconds(3));
        await cache.StatAsync("a.txt", CancellationToken.None);
        File.WriteAllBytes(share.Combine("a.txt"), [1, 2, 3]);
        await WaitUntilAsync(async () => (await cache.StatAsync("a.txt", CancellationToken.None)).Size == 3, TimeSpan.FromSeconds(2));
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
            AllowPlaintextTransport = true,
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
