using System.Collections.Concurrent;
using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>FsWatch notifications and FsTree walks of the Agent, over a raw <see cref="ProtocolConnection"/>.</summary>
[TestClass]
public sealed class AgentWatchTreeTests
{
    private static readonly TransportOptions Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(10) };

    private static AgentServer StartAgent(TempFolder share, AgentOptions? options = null)
    {
        var server = new AgentServer([new AgentShare("src", share.Path)], (options ?? new AgentOptions()) with { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        return server;
    }

    private static Task<ProtocolConnection> ConnectAsync(AgentServer server)
        => ProtocolConnection.OpenAsync(
            new TcpConnectionFactory("127.0.0.1", server.Port, Transport),
            new HelloRequest { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = "src" },
            Transport,
            CancellationToken.None);

    /// <summary>Subscribes and returns a queue the notifications land in.</summary>
    private static async Task<BlockingCollection<FsChangeNotification>> WatchAsync(ProtocolConnection connection)
    {
        var received = new BlockingCollection<FsChangeNotification>();
        connection.NotificationReceived += frame =>
        {
            if (frame.MessageType == MessageType.FsChanged)
                received.Add(ProtocolSerializer.Deserialize<FsChangeNotification>(frame.Header));
        };
        await connection.SendAsync(MessageType.FsWatch, ProtocolSerializer.Serialize(new FsWatchRequest()), default, CancellationToken.None);
        return received;
    }

    /// <summary>Waits until a notification satisfies <paramref name="match"/>; fails after <paramref name="timeout"/>.</summary>
    private static FsChangeNotification WaitFor(BlockingCollection<FsChangeNotification> received, Func<FsChangeNotification, bool> match, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !received.TryTake(out FsChangeNotification? notification, left))
                throw new AssertFailedException($"No matching notification within {timeout.TotalMilliseconds} ms.");
            if (match(notification))
                return notification;
        }
    }

    [TestMethod]
    public async Task Watch_FileWrittenOnAgent_NotificationArrives()
    {
        using var share = new TempFolder("watch");
        share.WriteFile(@"proj\a.cs", "a"u8.ToArray());
        await using AgentServer server = StartAgent(share);
        await using ProtocolConnection connection = await ConnectAsync(server);
        BlockingCollection<FsChangeNotification> received = await WatchAsync(connection);

        File.WriteAllText(share.Combine(@"proj\a.cs"), "changed");
        WaitFor(received, x => x.Changes.Any(c => c.Path == "proj/a.cs" && c.Action == FsChangeAction.Modified), TimeSpan.FromSeconds(5));

        File.Move(share.Combine(@"proj\a.cs"), share.Combine(@"proj\b.cs"));
        FsChangeNotification renamed = WaitFor(received, x => x.Changes.Any(c => c.Action == FsChangeAction.RenamedNewName), TimeSpan.FromSeconds(5));
        CollectionAssert.IsSubsetOf(
            new[] { "RenamedOldName proj/a.cs", "RenamedNewName proj/b.cs" },
            renamed.Changes.Select(c => $"{c.Action} {c.Path}").ToArray());
    }

    [TestMethod]
    public async Task Watch_BufferOverflow_SendsRescan()
    {
        using var share = new TempFolder("watch");
        await using AgentServer server = StartAgent(share);
        await using ProtocolConnection connection = await ConnectAsync(server);
        BlockingCollection<FsChangeNotification> received = await WatchAsync(connection);

        // FileSystemWatcher reports a ReadDirectoryChangesW overflow through its Error event with this exception.
        server.GetWatcher(new AgentShare("src", share.Path)).OnError(new InternalBufferOverflowException());

        FsChangeNotification rescan = WaitFor(received, x => x.Rescan, TimeSpan.FromSeconds(5));
        Assert.IsEmpty(rescan.Changes);
    }

    [TestMethod]
    public void LostWatcher_TellsSubscribers_AndRestartsWithRescan()
    {
        using var share = new TempFolder("watch");
        using var watcher = new ShareWatcher(share.Path, 64 * 1024, TimeSpan.FromMilliseconds(20), retryDelay: TimeSpan.FromMilliseconds(50));
        var received = new BlockingCollection<FsChangeNotification>();
        int lost = 0;
        using IDisposable subscription = watcher.Subscribe(received.Add, () => Interlocked.Increment(ref lost));

        // The root disappears and the watcher fails: it cannot be restarted until the root is back.
        Directory.Delete(share.Path, recursive: true);
        watcher.OnError(new IOException("The watcher stopped."));
        WaitFor(received, x => x.Rescan, TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref lost));

        Directory.CreateDirectory(share.Path);
        // A successful retry reports a rescan (what happened meanwhile is unknown) and watching works again.
        WaitFor(received, x => x.Rescan, TimeSpan.FromSeconds(5));
        share.WriteFile("back.txt", [1]);
        WaitFor(received, x => x.Changes.Any(c => c.Path == "back.txt"), TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public void Watch_RootGone_FailsInsteadOfPretendingToWatch()
    {
        using var share = new TempFolder("watch");
        Directory.Delete(share.Path, recursive: true);
        using var watcher = new ShareWatcher(share.Path, 64 * 1024, TimeSpan.FromMilliseconds(20));
        Assert.Throws<IOException>(() => watcher.Subscribe(_ => { }));
        Directory.CreateDirectory(share.Path);
        using IDisposable subscription = watcher.Subscribe(_ => { });
    }

    [TestMethod]
    public async Task Watch_TooManyChangesInOneBatch_BecomesRescan()
    {
        using var share = new TempFolder("watch");
        // The batch window opens at the first change and must outlast writing every file, otherwise the changes split
        // into several batches under the limit and no rescan happens (seen on slow CI runners with a 2 s window).
        await using AgentServer server = StartAgent(share, new AgentOptions { AllowPlaintextTransport = true, WatchBatchDelay = TimeSpan.FromSeconds(20) });
        await using ProtocolConnection connection = await ConnectAsync(server);
        BlockingCollection<FsChangeNotification> received = await WatchAsync(connection);

        string folder = share.Combine("many");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < ShareWatcher.MaxChangesPerBatch + 100; i++)
            File.WriteAllBytes(Path.Combine(folder, $"f{i}.txt"), []);

        // Either the batch limit or a real kernel buffer overflow turns it into a rescan; both are fine.
        WaitFor(received, x => x.Rescan, TimeSpan.FromSeconds(40));
    }

    [TestMethod]
    public async Task Watch_SecondSubscriberAfterFirstLeft_StillNotified()
    {
        using var share = new TempFolder("watch");
        await using AgentServer server = StartAgent(share);
        await using (ProtocolConnection first = await ConnectAsync(server))
            await WatchAsync(first);

        await using ProtocolConnection second = await ConnectAsync(server);
        BlockingCollection<FsChangeNotification> received = await WatchAsync(second);
        share.WriteFile("new.txt", [1]);
        WaitFor(received, x => x.Changes.Any(c => c.Path == "new.txt"), TimeSpan.FromSeconds(5));
    }

    private static TempFolder BuildTree()
    {
        var share = new TempFolder("tree");
        for (int d = 0; d < 5; d++)
        {
            for (int f = 0; f < 30; f++)
                share.WriteFile($@"lib{d}\file_with_a_rather_long_name_{f:D3}.cs", new byte[d * 10 + f]);
            for (int s = 0; s < 3; s++)
                share.WriteFile($@"lib{d}\sub{s}\deep\leaf{s}.txt", [1, 2, 3]);
        }
        Directory.CreateDirectory(share.Combine("empty"));
        share.WriteFile("root.txt", [9]);
        return share;
    }

    /// <summary>Follows every page; returns path → children names, with parts of one directory joined.</summary>
    private static async Task<(Dictionary<string, List<FsEntry>> Directories, bool Truncated, int Pages)> WalkAsync(ProtocolConnection connection, string path, int maxDepth)
    {
        var directories = new Dictionary<string, List<FsEntry>>(StringComparer.OrdinalIgnoreCase);
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong cursor = 0;
        int pages = 0;
        bool truncated;
        do
        {
            var request = new FsTreeRequest { Path = path, MaxDepth = maxDepth, Cursor = cursor };
            Frame frame = await connection.SendAsync(MessageType.FsTree, ProtocolSerializer.Serialize(request), default, CancellationToken.None);
            FsTreeResponse response = ProtocolSerializer.Deserialize<FsTreeResponse>(frame.Header);
            pages++;
            foreach (FsTreeDirectory directory in response.Directories)
            {
                Assert.DoesNotContain(directory.Path, complete, $"{directory.Path} listed again after it was complete.");
                if (!directories.TryGetValue(directory.Path, out List<FsEntry>? list))
                    directories[directory.Path] = list = [];
                list.AddRange(directory.Entries);
                if (directory.Complete)
                    complete.Add(directory.Path);
            }
            cursor = response.Cursor;
            truncated = response.Truncated;
        }
        while (cursor != 0);
        return (directories.Where(x => complete.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase), truncated, pages);
    }

    [TestMethod]
    public async Task Tree_SmallPages_MatchesRealTree()
    {
        using TempFolder share = BuildTree();
        // A tiny frame forces directories to be split over several pages.
        await using AgentServer server = StartAgent(share, new AgentOptions { AllowPlaintextTransport = true, MaxFrameSize = 4096, MaxReadSize = 1024 });
        await using ProtocolConnection connection = await ConnectAsync(server);

        (Dictionary<string, List<FsEntry>> tree, bool truncated, int pages) = await WalkAsync(connection, string.Empty, 0);

        Assert.IsFalse(truncated);
        Assert.IsGreaterThan(5, pages);
        var expected = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in Directory.EnumerateDirectories(share.Path, "*", SearchOption.AllDirectories).Append(share.Path))
        {
            string relative = Path.GetRelativePath(share.Path, dir).Replace('\\', '/');
            expected[relative == "." ? string.Empty : relative] = Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToArray()!;
        }
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), tree.Keys.ToArray());
        foreach ((string dir, string[] names) in expected)
            CollectionAssert.AreEqual(names, tree[dir].Select(x => x.Name).ToArray(), dir);

        FsEntry file = tree["lib3"].Single(x => x.Name == "file_with_a_rather_long_name_007.cs");
        Assert.AreEqual(37L, file.Size);
        Assert.IsTrue(tree[string.Empty].Single(x => x.Name == "empty").IsDirectory);
        Assert.IsEmpty(tree["empty"]);
    }

    [TestMethod]
    public async Task Tree_MaxDepth_StopsBelowIt()
    {
        using TempFolder share = BuildTree();
        await using AgentServer server = StartAgent(share);
        await using ProtocolConnection connection = await ConnectAsync(server);

        (Dictionary<string, List<FsEntry>> tree, _, _) = await WalkAsync(connection, "lib1", 2);

        CollectionAssert.AreEquivalent(new[] { "lib1", "lib1/sub0", "lib1/sub1", "lib1/sub2" }, tree.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "deep" }, tree["lib1/sub0"].Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task Tree_EntryLimit_ReportsTruncated()
    {
        using TempFolder share = BuildTree();
        await using AgentServer server = StartAgent(share, new AgentOptions { AllowPlaintextTransport = true, MaxTreeEntries = 50 });
        await using ProtocolConnection connection = await ConnectAsync(server);

        (Dictionary<string, List<FsEntry>> tree, bool truncated, _) = await WalkAsync(connection, string.Empty, 0);

        Assert.IsTrue(truncated);
        Assert.IsLessThanOrEqualTo(50, tree.Values.Sum(x => x.Count));
    }

    [TestMethod]
    public async Task Tree_JunctionInside_IsListedButNotDescended()
    {
        using TempFolder share = BuildTree();
        using var outside = new TempFolder("outside");
        outside.WriteFile("secret.txt", [1]);
        share.CreateJunction("link", outside.Path);
        await using AgentServer server = StartAgent(share);
        await using ProtocolConnection connection = await ConnectAsync(server);

        (Dictionary<string, List<FsEntry>> tree, _, _) = await WalkAsync(connection, string.Empty, 0);

        Assert.IsTrue(tree[string.Empty].Any(x => x.Name == "link"));
        Assert.IsFalse(tree.ContainsKey("link"));
    }

    [TestMethod]
    public async Task Tree_UnknownCursor_IsInvalidParameter()
    {
        using var share = new TempFolder("tree");
        await using AgentServer server = StartAgent(share);
        await using ProtocolConnection connection = await ConnectAsync(server);

        var error = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => connection.SendAsync(
            MessageType.FsTree, ProtocolSerializer.Serialize(new FsTreeRequest { Path = string.Empty, Cursor = 12345 }), default, CancellationToken.None));
        Assert.AreEqual(NtStatus.InvalidParameter, error.Status);
    }
}
