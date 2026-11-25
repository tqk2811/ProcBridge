using System.Net;
using System.Security.Cryptography;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>Agent and Broker (AgentClient + FileCache) talking over loopback TCP.</summary>
[TestClass]
public sealed class BrokerEndToEndTests
{
    private static readonly TransportOptions Transport = new()
    {
        ConnectionCount = 4,
        ReadChunkSize = 64 * 1024,
        MaxParallelChunks = 8,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(10),
    };

    private static AgentServer StartAgent(TempFolder share, int port = 0)
    {
        var server = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = port });
        server.Start();
        return server;
    }

    private static AgentClient CreateClient(int port, TransportOptions? options = null)
        => new(new TcpConnectionFactory("127.0.0.1", port, options ?? Transport), "src", options ?? Transport);

    private static byte[] RandomBytes(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    [TestMethod]
    public async Task ListAndStat_MatchShare()
    {
        using var share = new TempFolder("e2e");
        share.WriteFile(@"proj\a.cs", "class A {}"u8.ToArray());
        share.WriteFile(@"proj\b.txt", RandomBytes(10));
        Directory.CreateDirectory(share.Combine(@"proj\sub"));
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, TimeSpan.FromSeconds(30));

        IReadOnlyList<FsEntry> list = await cache.ListAsync("proj", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "a.cs", "b.txt", "sub" }, list.Select(x => x.Name).ToArray());
        Assert.IsTrue(list[2].IsDirectory);

        FsEntry stat = await cache.StatAsync("proj/b.txt", CancellationToken.None);
        Assert.AreEqual(10L, stat.Size);

        var missing = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => cache.StatAsync("proj/nope", CancellationToken.None));
        Assert.AreEqual(NtStatus.ObjectNameNotFound, missing.Status);
    }

    [TestMethod]
    public async Task LargeFile_DownloadsInParallelOverSeveralConnections_HashMatches()
    {
        using var share = new TempFolder("e2e");
        byte[] content = RandomBytes(8 * 1024 * 1024 + 12345);
        string source = share.WriteFile(@"big\data.bin", content);
        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Archive);
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var written = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetCreationTimeUtc(source, created);
        File.SetLastWriteTimeUtc(source, written);
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, TimeSpan.FromSeconds(30));

        CachedItem item = await cache.EnsureAsync("big/data.bin", CancellationToken.None);

        Assert.AreEqual(cacheDir.Combine(@"big\data.bin"), item.LocalPath);
        CollectionAssert.AreEqual(SHA256.HashData(content), SHA256.HashData(File.ReadAllBytes(item.LocalPath)));
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(item.LocalPath));
        Assert.AreEqual(created, File.GetCreationTimeUtc(item.LocalPath));
        Assert.IsTrue(File.GetAttributes(item.LocalPath).HasFlag(FileAttributes.ReadOnly));

        long[] perSlot = client.Pool.GetRequestsPerSlot();
        int usedConnections = perSlot.Count(x => x > 0);
        Assert.IsGreaterThan(1, usedConnections, "Chunks should spread over more than one connection: " + string.Join(",", perSlot));
        Assert.IsGreaterThanOrEqualTo(content.Length / Transport.ReadChunkSize, perSlot.Sum());
    }

    [TestMethod]
    public async Task CachedFile_IsReusedUntilVersionChanges()
    {
        using var share = new TempFolder("e2e");
        string source = share.WriteFile("f.txt", "one"u8.ToArray());
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, TimeSpan.Zero);

        CachedItem first = await cache.EnsureAsync("f.txt", CancellationToken.None);
        long requestsAfterFirst = client.Pool.GetRequestsPerSlot().Sum();

        CachedItem again = await cache.EnsureAsync("f.txt", CancellationToken.None);
        Assert.AreEqual(first.LocalPath, again.LocalPath);
        // Only the stat (TTL 0) went to machine B, no read.
        Assert.AreEqual(requestsAfterFirst + 1, client.Pool.GetRequestsPerSlot().Sum());

        File.WriteAllBytes(source, "second version"u8.ToArray());
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        CachedItem updated = await cache.EnsureAsync("f.txt", CancellationToken.None);
        Assert.AreEqual("second version", File.ReadAllText(updated.LocalPath));
    }

    [TestMethod]
    public async Task ConcurrentEnsure_SharesOneDownload()
    {
        using var share = new TempFolder("e2e");
        share.WriteFile("f.bin", RandomBytes(512 * 1024));
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, TimeSpan.FromSeconds(30));

        CachedItem[] items = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.EnsureAsync("f.bin", CancellationToken.None)));
        Assert.IsTrue(items.All(x => x.LocalPath == items[0].LocalPath));
        // 1 stat + 8 chunks of 64 KiB; a second download would add 8 more reads.
        Assert.AreEqual(9L, client.Pool.GetRequestsPerSlot().Sum());
    }

    [TestMethod]
    public async Task TraversalThroughBroker_IsRejectedByAgent()
    {
        using var share = new TempFolder("e2e");
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);

        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.StatAsync("../Windows", CancellationToken.None));
        Assert.AreEqual(NtStatus.ObjectNameInvalid, e.Status);
    }

    [TestMethod]
    public async Task AgentRestart_ClientReconnects()
    {
        using var share = new TempFolder("e2e");
        share.WriteFile("f.txt", "x"u8.ToArray());
        var options = Transport with { ConnectTimeout = TimeSpan.FromSeconds(2), MaxRetries = 1 };

        AgentServer server = StartAgent(share);
        int port = server.Port;
        await using AgentClient client = CreateClient(port, options);
        Assert.AreEqual(1L, (await client.StatAsync("f.txt", CancellationToken.None)).Size);
        long connectsBefore = client.Pool.ConnectCount;

        await server.DisposeAsync();
        var down = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.StatAsync("f.txt", CancellationToken.None));
        Assert.IsTrue(down.IsConnectionFailure, down.ToString());

        await using AgentServer restarted = StartAgent(share, port);
        Assert.AreEqual(1L, (await client.StatAsync("f.txt", CancellationToken.None)).Size);
        Assert.IsGreaterThan(connectsBefore, client.Pool.ConnectCount);
    }

    [TestMethod]
    public async Task BrokenConnection_IdempotentRequestIsRetriedOnNewConnection()
    {
        using var share = new TempFolder("e2e");
        share.WriteFile("f.txt", "x"u8.ToArray());
        AgentServer server = StartAgent(share);
        int port = server.Port;
        await using AgentClient client = CreateClient(port);
        await client.StatAsync("f.txt", CancellationToken.None);

        // Restart quickly: the pooled connection is dead, but the retry opens a new one transparently.
        await server.DisposeAsync();
        await using AgentServer restarted = StartAgent(share, port);
        Assert.AreEqual(1L, (await client.StatAsync("f.txt", CancellationToken.None)).Size);
    }
}
