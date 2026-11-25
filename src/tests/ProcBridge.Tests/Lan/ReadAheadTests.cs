using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>Read-ahead of the small files of a directory when one of them is opened.</summary>
[TestClass]
public sealed class ReadAheadTests
{
    private const int Threshold = 1000;

    private static readonly TransportOptions Transport = new() { ConnectionCount = 4, ConnectTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(10) };

    private static AgentServer StartAgent(TempFolder share)
    {
        var server = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        return server;
    }

    private static AgentClient CreateClient(int port) => new(new TcpConnectionFactory("127.0.0.1", port, Transport), "src", Transport);

    private static TempFolder BuildShare()
    {
        var share = new TempFolder("ra");
        for (int i = 0; i < 10; i++)
            share.WriteFile($@"dir\small{i}.cs", new byte[100 + i]);
        share.WriteFile(@"dir\edge.cs", new byte[Threshold]);
        share.WriteFile(@"dir\big.bin", new byte[Threshold + 1]);
        share.WriteFile(@"dir\huge.bin", new byte[200_000]);
        share.WriteFile(@"dir\sub\nested.cs", new byte[10]);
        share.WriteFile(@"other\x.cs", new byte[10]);
        return share;
    }

    [TestMethod]
    public async Task OpenSmallFile_PrefetchesSmallSiblingsOnly()
    {
        using TempFolder share = BuildShare();
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, new FileCacheOptions { ReadAheadMaxFileSize = Threshold, ReadAheadMaxFiles = 100 });

        await cache.EnsureAsync("dir/small0.cs", CancellationToken.None);
        await cache.WaitForReadAheadAsync().WaitAsync(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 10; i++)
            Assert.IsTrue(File.Exists(cache.GetLocalPath($"dir/small{i}.cs")), $"small{i}.cs");
        Assert.IsTrue(File.Exists(cache.GetLocalPath("dir/edge.cs")), "A file exactly at the threshold is read ahead.");
        Assert.IsFalse(File.Exists(cache.GetLocalPath("dir/big.bin")), "Files above the threshold must not be read ahead.");
        Assert.IsFalse(File.Exists(cache.GetLocalPath("dir/huge.bin")));
        Assert.IsFalse(File.Exists(cache.GetLocalPath("dir/sub/nested.cs")), "Read-ahead does not descend into subdirectories.");
        Assert.IsFalse(File.Exists(cache.GetLocalPath("other/x.cs")));
        // small0 was the foreground open; 9 siblings + edge were prefetched.
        Assert.AreEqual(10, cache.ReadAheadDownloads);

        // A prefetched file is then served from the cache without a download.
        long before = client.Pool.GetRequestsPerSlot().Sum();
        CachedItem item = await cache.EnsureAsync("dir/small5.cs", CancellationToken.None);
        Assert.AreEqual(105L, new FileInfo(item.LocalPath).Length);
        Assert.AreEqual(before, client.Pool.GetRequestsPerSlot().Sum());
    }

    [TestMethod]
    public async Task OpenBigFile_DoesNotReadAhead()
    {
        using TempFolder share = BuildShare();
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, new FileCacheOptions { ReadAheadMaxFileSize = Threshold });

        await cache.EnsureAsync("dir/huge.bin", CancellationToken.None);
        await cache.WaitForReadAheadAsync();

        Assert.AreEqual(0, cache.ReadAheadDownloads);
        Assert.IsFalse(File.Exists(cache.GetLocalPath("dir/small1.cs")));
    }

    [TestMethod]
    public async Task MaxFiles_LimitsOneDirectory()
    {
        using TempFolder share = BuildShare();
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, new FileCacheOptions { ReadAheadMaxFileSize = Threshold, ReadAheadMaxFiles = 3 });

        await cache.EnsureAsync("dir/small0.cs", CancellationToken.None);
        await cache.WaitForReadAheadAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsLessThanOrEqualTo(3, cache.ReadAheadDownloads);
        Assert.AreEqual(4, Directory.GetFiles(cache.GetLocalPath("dir")).Length, "The foreground file plus at most 3 read ahead.");
    }

    [TestMethod]
    public async Task Disabled_DownloadsOnlyWhatIsOpened()
    {
        using TempFolder share = BuildShare();
        using var cacheDir = new TempFolder("cache");
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, new FileCacheOptions { ReadAheadMaxFileSize = 0 });

        await cache.EnsureAsync("dir/small0.cs", CancellationToken.None);
        await cache.WaitForReadAheadAsync();

        CollectionAssert.AreEqual(new[] { "small0.cs" }, Directory.GetFiles(cache.GetLocalPath("dir")).Select(Path.GetFileName).ToArray());
    }
}
