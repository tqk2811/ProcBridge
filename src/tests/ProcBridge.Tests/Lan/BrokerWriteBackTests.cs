using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Tests.Secure;

namespace ProcBridge.Tests.Lan;

/// <summary>
/// M3 Broker side played by <see cref="FakeHook"/>: creating on machine B, write tokens, write-back on close, conflicts,
/// the persistent queue, rename, delete and attribute changes (plan 4.5 and 10). Runs over the encrypted transport
/// (paired <see cref="SecurePeers"/>), like production.
/// </summary>
[TestClass]
public sealed class BrokerWriteBackTests
{
    private const string VirtualRoot = @"V:\Remote\Proj";
    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;

    private sealed class Session : IAsyncDisposable
    {
        private Session(SecurePeers peers, TempFolder share, TempFolder work, AgentServer agent, BrokerHost broker, FakeHook hook)
        {
            Peers = peers;
            Share = share;
            Work = work;
            Agent = agent;
            Broker = broker;
            Hook = hook;
        }

        public SecurePeers Peers { get; }
        public TempFolder Share { get; }
        public TempFolder Work { get; }
        public AgentServer Agent { get; private set; }
        public BrokerHost Broker { get; private set; }
        public FakeHook Hook { get; private set; }

        public static async Task<Session> StartAsync(ConflictPolicy policy = ConflictPolicy.KeepBoth, TimeSpan? retryDelay = null)
        {
            var peers = new SecurePeers();
            var share = new TempFolder("wshare");
            var work = new TempFolder("wwork");
            AgentServer agent = StartAgent(peers, share, 0);
            (BrokerHost broker, FakeHook hook) = await StartBrokerAsync(peers, work, agent.Port, policy, retryDelay);
            return new Session(peers, share, work, agent, broker, hook);
        }

        public static AgentServer StartAgent(SecurePeers peers, TempFolder share, int port)
        {
            var agent = new AgentServer([new AgentShare("src", share.Path, writable: true)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = port }, peers.Agent());
            agent.Start();
            return agent;
        }

        public static async Task<(BrokerHost, FakeHook)> StartBrokerAsync(SecurePeers peers, TempFolder work, int port, ConflictPolicy policy = ConflictPolicy.KeepBoth, TimeSpan? retryDelay = null)
        {
            string session = Guid.NewGuid().ToString("N");
            BrokerHost broker = BrokerHost.Start(new BrokerOptions
            {
                SessionId = session, AgentHost = "127.0.0.1", AgentPort = port, ShareName = "src",
                Security = peers.Broker,
                Mappings = [new VirtualMapping(VirtualRoot, "")],
                CacheRoot = Path.Combine(work.Path, "cache"),
                WriteQueueRoot = Path.Combine(work.Path, "queue"),
                ConflictPolicy = policy,
                WriteRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200),
                Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(2), ConnectBackoff = TimeSpan.Zero },
                // These tests change machine B's file while a writer has it open on machine A (conflicts): the handle
                // machine B keeps for writers (M8 share mode) would refuse that, so it is only opened for locks here.
                HoldShareMode = false,
            });
            FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
            await hook.CallOkAsync(new HookHelloRequest { ProcessId = 1, Architecture = HookArchitecture.X64 }, HookHelloReply.Read);
            return (broker, hook);
        }

        public async Task RestartBrokerAsync(int port)
        {
            await Hook.DisposeAsync();
            await Broker.DisposeAsync();
            (Broker, Hook) = await StartBrokerAsync(Peers, Work, port);
        }

        public async Task StopAgentAsync() => await Agent.DisposeAsync();

        public void StartAgentAgain(int port) => Agent = StartAgent(Peers, Share, port);

        public Task<HookResolveReply> ResolveAsync(string path, uint access, uint disposition, uint options = 0)
            => Hook.CallOkAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = path, DesiredAccess = access, CreateDisposition = disposition, CreateOptions = options }, HookResolveReply.Read);

        public async Task CloseAsync(ulong token)
            => await Hook.CallOkAsync(new HookTokenRequest { Type = HookIpcMessageType.Close, WriteToken = token }, (ref HookIpcReader _) => 0);

        public async Task FlushAsync(ulong token)
            => await Hook.CallOkAsync(new HookTokenRequest { Type = HookIpcMessageType.Flush, WriteToken = token }, (ref HookIpcReader _) => 0);

        public async Task WaitQueueEmptyAsync()
        {
            for (int i = 0; i < 200 && Broker.WriteBack.PendingCount > 0; i++)
                await Task.Delay(100);
            Assert.AreEqual(0, Broker.WriteBack.PendingCount);
        }

        public async ValueTask DisposeAsync()
        {
            await Hook.DisposeAsync();
            await Broker.DisposeAsync();
            await Agent.DisposeAsync();
            Work.Dispose();
            Share.Dispose();
            Peers.Dispose();
        }
    }

    [TestMethod]
    public async Task OpenForWrite_ChangeElsewhereDuringStat_AsksMachineBOnlyOnce()
    {
        // Another file changing while the stat is in flight (a build writing its outputs) keeps that stat out of the
        // cache; the open must still use it instead of asking machine B again (one round trip per request).
        await using Session s = await Session.StartAsync();
        // Above the read-ahead size: no background listing of the folder joins the count.
        s.Share.WriteFile("a.txt", new byte[100 * 1024]);
        FileCache cache = s.Broker.Cache;
        Assert.IsTrue(await cache.IsWritableAsync(CancellationToken.None)); // asked once per session, not part of the open
        cache.BeforeMetadataStore = () => cache.Invalidate("elsewhere.txt");
        long before = s.Broker.Client.Pool.GetRequestsPerSlot().Sum();

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);

        cache.BeforeMetadataStore = null;
        Assert.AreEqual(2, s.Broker.Client.Pool.GetRequestsPerSlot().Sum() - before, "one stat and one read");
        await s.CloseAsync(open.WriteToken);
    }

    [TestMethod]
    public async Task CreateNewFile_IsCreatedOnMachineBFirst_AndWrittenBackOnClose()
    {
        await using Session s = await Session.StartAsync();

        // The parent is missing on machine B: the create fails as on a local volume.
        Assert.AreEqual((uint)NtStatus.ObjectPathNotFound, (await s.Hook.CallAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = @"dir\new.txt", DesiredAccess = GenericWrite, CreateDisposition = 2 })).Status);

        HookResolveReply dir = await s.ResolveAsync("dir", GenericRead, 2, 0x1 /* FILE_DIRECTORY_FILE */);
        Assert.IsTrue(Directory.Exists(s.Share.Combine("dir")));
        Assert.AreEqual(0ul, dir.WriteToken);
        Assert.IsFalse(Directory.Exists(dir.CachePath), "the hook's real call creates the cache folder");

        HookResolveReply created = await s.ResolveAsync(@"dir\new.txt", GenericWrite, 2);
        Assert.IsTrue(File.Exists(s.Share.Combine(@"dir\new.txt")));
        Assert.AreNotEqual(0ul, created.WriteToken);
        Directory.CreateDirectory(dir.CachePath);
        File.WriteAllText(created.CachePath, "written on A");
        await s.CloseAsync(created.WriteToken);

        Assert.AreEqual("written on A", File.ReadAllText(s.Share.Combine(@"dir\new.txt")));
        Assert.AreEqual(0, s.Broker.WriteBack.PendingCount);
        HookListReply list = await s.Hook.CallOkAsync(new HookListRequest { MappingIndex = 0, RelativePath = "dir" }, HookListReply.Read);
        Assert.AreEqual(12L, list.Entries.Single().Info.Size, "listing sees the written size at once");
        Assert.AreEqual((uint)NtStatus.ObjectNameCollision, (await s.Hook.CallAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = @"dir\new.txt", DesiredAccess = GenericWrite, CreateDisposition = 2 })).Status);
    }

    [TestMethod]
    public async Task OverwriteExisting_IsNotDownloaded_AndUnchangedCloseUploadsNothing()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "original content"u8.ToArray());

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        Assert.AreEqual("original content", File.ReadAllText(open.CachePath));
        Assert.IsFalse(new FileInfo(open.CachePath).IsReadOnly, "a writable share does not force read-only cache copies");
        await s.CloseAsync(open.WriteToken);
        Assert.AreEqual("original content", File.ReadAllText(remote));
        DateTime written = File.GetLastWriteTimeUtc(remote);

        HookResolveReply overwrite = await s.ResolveAsync("a.txt", GenericWrite, 5 /* FILE_OVERWRITE_IF */);
        File.WriteAllText(overwrite.CachePath, "new");
        await s.CloseAsync(overwrite.WriteToken);

        Assert.AreEqual("new", File.ReadAllText(remote));
        Assert.AreNotEqual(written, File.GetLastWriteTimeUtc(remote));
    }

    [TestMethod]
    public async Task ChangeOnMachineBWhileOpenOnA_IsDetected_AndBothVersionsKept()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(remote, "edited on machine B");
        File.WriteAllText(open.CachePath, "edited on machine A");
        await s.CloseAsync(open.WriteToken);

        Assert.AreEqual("edited on machine B", File.ReadAllText(remote));
        WriteConflict conflict = s.Broker.WriteBack.Conflicts.Single();
        Assert.AreEqual("a.txt", conflict.RemotePath);
        Assert.AreEqual("edited on machine A", File.ReadAllText(s.Share.Combine(conflict.ConflictPath)));

        // The next open sees machine B's version again.
        HookResolveReply reopen = await s.ResolveAsync("a.txt", GenericRead, 1);
        Assert.AreEqual("edited on machine B", File.ReadAllText(reopen.CachePath));
    }

    [TestMethod]
    public async Task OverwritePolicy_LetsMachineAWin()
    {
        await using Session s = await Session.StartAsync(ConflictPolicy.Overwrite);
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(remote, "edited on machine B");
        File.WriteAllText(open.CachePath, "edited on machine A");
        await s.CloseAsync(open.WriteToken);

        Assert.AreEqual("edited on machine A", File.ReadAllText(remote));
        Assert.IsEmpty(s.Broker.WriteBack.Conflicts);
    }

    [TestMethod]
    public async Task AgentGoneAtClose_EntryStaysQueued_AndNextBrokerSendsIt()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());
        int port = s.Agent.Port;

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(open.CachePath, "saved while offline");
        await s.StopAgentAsync();
        await s.CloseAsync(open.WriteToken);

        Assert.AreEqual(1, s.Broker.WriteBack.PendingCount);
        Assert.AreEqual("base", File.ReadAllText(remote));

        // The Broker dies with the entry still queued; a new one for the same Agent and share sends it.
        s.StartAgentAgain(port);
        await s.RestartBrokerAsync(port);
        await s.Broker.WriteBack.Recovery.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual("saved while offline", File.ReadAllText(remote));
        Assert.AreEqual(0, s.Broker.WriteBack.PendingCount);
    }

    [TestMethod]
    public async Task AgentBackWhileBrokerRuns_RetryLoopSendsQueuedEntry()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());
        int port = s.Agent.Port;

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(open.CachePath, "late");
        await s.StopAgentAsync();
        await s.CloseAsync(open.WriteToken);
        s.StartAgentAgain(port);

        for (int i = 0; i < 100 && s.Broker.WriteBack.PendingCount > 0; i++)
            await Task.Delay(100);
        Assert.AreEqual("late", File.ReadAllText(remote));
    }

    [TestMethod]
    public async Task RenameDeleteAndSetInfo_GoToMachineB()
    {
        await using Session s = await Session.StartAsync();
        s.Share.WriteFile("a.txt", "a"u8.ToArray());
        s.Share.WriteFile(@"full\x.txt", "x"u8.ToArray());
        await s.ResolveAsync("a.txt", GenericRead, 1);

        HookRenameReply renamed = await s.Hook.CallOkAsync(new HookRenameRequest
        {
            MappingIndex = 0, NewMappingIndex = 0, RelativePath = "a.txt", NewRelativePath = @"full\b.txt",
        }, HookRenameReply.Read);
        Assert.IsTrue(File.Exists(s.Share.Combine(@"full\b.txt")));
        Assert.IsFalse(File.Exists(s.Share.Combine("a.txt")));
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(renamed.NewCachePath)));
        StringAssert.EndsWith(renamed.NewCachePath, @"full\b.txt");
        Assert.AreEqual((uint)NtStatus.ObjectNameCollision, (await s.Hook.CallAsync(new HookRenameRequest
        {
            MappingIndex = 0, NewMappingIndex = 0, RelativePath = @"full\b.txt", NewRelativePath = @"full\x.txt",
        })).Status);

        long time = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        HookStatReply set = await s.Hook.CallOkAsync(new HookSetInfoRequest
        {
            MappingIndex = 0, RelativePath = @"full\b.txt", Fields = HookSetInfoFields.LastWriteTime | HookSetInfoFields.Attributes,
            LastWriteTime = time, Attributes = 0x2,
        }, HookStatReply.Read);
        Assert.AreEqual(time, set.Info.LastWriteTime);
        Assert.AreEqual(time, File.GetLastWriteTimeUtc(s.Share.Combine(@"full\b.txt")).ToFileTimeUtc());
        Assert.IsTrue(File.GetAttributes(s.Share.Combine(@"full\b.txt")).HasFlag(FileAttributes.Hidden));

        Assert.AreEqual((uint)NtStatus.DirectoryNotEmpty, (await s.Hook.CallAsync(new HookDeleteRequest { MappingIndex = 0, RelativePath = "full" })).Status);
        await s.Hook.CallOkAsync(new HookDeleteRequest { MappingIndex = 0, RelativePath = @"full\x.txt" }, (ref HookIpcReader _) => 0);
        Assert.IsFalse(File.Exists(s.Share.Combine(@"full\x.txt")));
        HookListReply list = await s.Hook.CallOkAsync(new HookListRequest { MappingIndex = 0, RelativePath = "full" }, HookListReply.Read);
        CollectionAssert.AreEqual(new[] { "b.txt" }, list.Entries.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task SetInfo_WaitsForTheWriteBackOfTheSameFile()
    {
        // Machine B's commit reads the attributes, replaces the file (which carries the old attributes over) and puts
        // read-only back: an attribute change landing in between was undone (WriteScenario: "attributes normal" read 0x3).
        await using Session s = await Session.StartAsync();
        s.Share.WriteFile("a.txt", "a"u8.ToArray());
        await s.ResolveAsync("a.txt", GenericRead, 1);

        Task<HookStatReply> set;
        using (await s.Broker.WriteBack.LockPathsAsync(["a.txt"], CancellationToken.None))
        {
            // Stands for an entry of a.txt being sent right now.
            set = s.Hook.CallOkAsync(new HookSetInfoRequest
            {
                MappingIndex = 0, RelativePath = "a.txt", Fields = HookSetInfoFields.Attributes, Attributes = 0x2,
            }, HookStatReply.Read);
            await Task.Delay(300);
            Assert.IsFalse(set.IsCompleted, "SetInfo went to machine B in the middle of a write-back of the same file.");
            Assert.IsFalse(File.GetAttributes(s.Share.Combine("a.txt")).HasFlag(FileAttributes.Hidden));
        }
        await set.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(File.GetAttributes(s.Share.Combine("a.txt")).HasFlag(FileAttributes.Hidden));
    }

    [TestMethod]
    public async Task SetFileTimeOnRewrittenSameSizeFile_StillUploadsNewContent()
    {
        // CopyFile over a file of the same size: CREATE_ALWAYS, write, then SetFileTime with the source's time.
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "aaaa"u8.ToArray());
        long sourceTime = new DateTime(2015, 3, 3, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericWrite, 5 /* FILE_OVERWRITE_IF */);
        File.WriteAllText(open.CachePath, "bbbb");
        File.SetLastWriteTimeUtc(open.CachePath, DateTime.FromFileTimeUtc(sourceTime));
        await s.Hook.CallOkAsync(new HookSetInfoRequest
        {
            MappingIndex = 0, RelativePath = "a.txt", Fields = HookSetInfoFields.LastWriteTime, LastWriteTime = sourceTime,
        }, HookStatReply.Read);
        await s.CloseAsync(open.WriteToken);

        Assert.AreEqual("bbbb", File.ReadAllText(remote));
        Assert.AreEqual(sourceTime, File.GetLastWriteTimeUtc(remote).ToFileTimeUtc());
        Assert.IsEmpty(s.Broker.WriteBack.Conflicts);
    }

    [TestMethod]
    public async Task LargeFile_IsUploadedInParallelBlocks()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("big.bin", []);
        byte[] content = new byte[4 * 1024 * 1024 + 123];
        new Random(2).NextBytes(content);

        HookResolveReply open = await s.ResolveAsync("big.bin", GenericWrite, 5);
        File.WriteAllBytes(open.CachePath, content);
        await s.CloseAsync(open.WriteToken).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(0, s.Broker.WriteBack.PendingCount);
        CollectionAssert.AreEqual(content, File.ReadAllBytes(remote));
    }

    [TestMethod]
    public async Task EntryRetriedWhileFileOpen_MovesTheBase_AndNewerEntryReplacesUnsentOne()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());
        int port = s.Agent.Port;

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        await s.StopAgentAsync();
        File.WriteAllText(open.CachePath, "v1");
        await s.FlushAsync(open.WriteToken);
        File.WriteAllText(open.CachePath, "v2 longer");
        await s.FlushAsync(open.WriteToken);
        Assert.AreEqual(1, s.Broker.WriteBack.PendingCount, "the newer content replaces the unsent entry");

        s.StartAgentAgain(port);
        await s.WaitQueueEmptyAsync();
        Assert.AreEqual("v2 longer", File.ReadAllText(remote));

        // The retry moved the version the open file compares with: no false conflict on close.
        File.WriteAllText(open.CachePath, "v3");
        await s.CloseAsync(open.WriteToken);
        Assert.AreEqual("v3", File.ReadAllText(remote));
        Assert.IsEmpty(s.Broker.WriteBack.Conflicts);
    }

    [TestMethod]
    public async Task RefusedDelete_KeepsOpenWriterWritingBack()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.SetAttributes(remote, FileAttributes.ReadOnly);
        Assert.AreEqual((uint)NtStatus.CannotDelete, (await s.Hook.CallAsync(new HookDeleteRequest { MappingIndex = 0, RelativePath = "a.txt" })).Status);
        File.SetAttributes(remote, FileAttributes.Normal);
        File.WriteAllText(open.CachePath, "still mine");
        await s.CloseAsync(open.WriteToken);

        Assert.AreEqual("still mine", File.ReadAllText(remote));
    }

    [TestMethod]
    public async Task DeleteWhileEntryQueued_DropsTheEntry()
    {
        await using Session s = await Session.StartAsync(retryDelay: TimeSpan.FromHours(1));
        s.Share.WriteFile("a.txt", "base"u8.ToArray());
        int port = s.Agent.Port;

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(open.CachePath, "offline");
        await s.StopAgentAsync();
        await s.CloseAsync(open.WriteToken);
        Assert.AreEqual(1, s.Broker.WriteBack.PendingCount);
        s.StartAgentAgain(port);

        await s.Hook.CallOkAsync(new HookDeleteRequest { MappingIndex = 0, RelativePath = "a.txt" }, (ref HookIpcReader _) => 0);
        await s.Broker.WriteBack.ResendPendingAsync(CancellationToken.None);

        Assert.AreEqual(0, s.Broker.WriteBack.PendingCount);
        Assert.IsEmpty(Directory.GetFileSystemEntries(s.Share.Path), "the deleted file does not come back, not even as a conflict copy");
    }

    [TestMethod]
    public async Task RenameWhileEntryQueued_SendsItToTheNewName()
    {
        await using Session s = await Session.StartAsync(retryDelay: TimeSpan.FromHours(1));
        s.Share.WriteFile("a.txt", "base"u8.ToArray());
        int port = s.Agent.Port;

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(open.CachePath, "offline");
        await s.StopAgentAsync();
        await s.CloseAsync(open.WriteToken);
        s.StartAgentAgain(port);

        await s.Hook.CallOkAsync(new HookRenameRequest
        {
            MappingIndex = 0, NewMappingIndex = 0, RelativePath = "a.txt", NewRelativePath = "b.txt",
        }, HookRenameReply.Read);
        await s.Broker.WriteBack.ResendPendingAsync(CancellationToken.None);

        Assert.AreEqual(0, s.Broker.WriteBack.PendingCount);
        CollectionAssert.AreEqual(new[] { "b.txt" }, Directory.GetFileSystemEntries(s.Share.Path).Select(Path.GetFileName).ToArray());
        Assert.AreEqual("offline", File.ReadAllText(s.Share.Combine("b.txt")));
    }

    [TestMethod]
    public async Task DeleteOnCloseWithExpectedFileId_LeavesAFileThatTookTheName()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "opened"u8.ToArray());
        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | 0x00010000 /* DELETE */, 1, 0x00001000 /* FILE_DELETE_ON_CLOSE */);
        // Another file replaced it on machine B before the handle was closed.
        s.Share.WriteFile("b.txt", "replacement"u8.ToArray());
        File.Move(s.Share.Combine("b.txt"), remote, overwrite: true);

        await s.Hook.CallOkAsync(new HookDeleteRequest
        {
            MappingIndex = 0, RelativePath = "a.txt", Flags = HookDeleteRequest.FlagKeepItem | HookDeleteRequest.FlagExpectFileId,
            ExpectedFileId = open.Info.FileId,
        }, (ref HookIpcReader _) => 0);

        Assert.AreNotEqual(0ul, open.Info.FileId);
        Assert.AreEqual("replacement", File.ReadAllText(remote));
    }

    [TestMethod]
    public async Task ConflictedFile_LastCloseKeepsTheFinalVersion()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());

        HookResolveReply first = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        HookResolveReply second = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        File.WriteAllText(remote, "edited on machine B");
        File.WriteAllText(first.CachePath, "A first");
        await s.CloseAsync(first.WriteToken);
        File.WriteAllText(second.CachePath, "A final version");
        await s.CloseAsync(second.WriteToken);

        Assert.AreEqual("edited on machine B", File.ReadAllText(remote));
        WriteConflict[] conflicts = s.Broker.WriteBack.Conflicts.ToArray();
        Assert.HasCount(2, conflicts);
        Assert.AreEqual("A final version", File.ReadAllText(s.Share.Combine(conflicts[^1].ConflictPath)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(50);
        Assert.IsTrue(condition(), message);
    }

    /// <summary>Completes when the cache applied a change notification that names <paramref name="path"/>.</summary>
    private static Task WaitForNotificationAsync(FileCache cache, string path)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(FsChangeNotification notification)
        {
            if (notification.Changes.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                cache.RemoteChanged -= OnChanged;
                seen.TrySetResult();
            }
        }
        cache.RemoteChanged += OnChanged;
        return seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task WriteFromOneMachineA_IsSeenByAnotherThroughTheWatch()
    {
        await using Session s = await Session.StartAsync();
        s.Share.WriteFile("a.txt", "base"u8.ToArray());
        using var otherWork = new TempFolder("wwork2");
        (BrokerHost other, FakeHook otherHook) = await Session.StartBrokerAsync(s.Peers, otherWork, s.Agent.Port);
        try
        {
            await WaitUntilAsync(() => s.Broker.Cache.IsWatchActive && other.Cache.IsWatchActive, "both Brokers watch the share");
            var read = new HookResolveRequest { MappingIndex = 0, RelativePath = "a.txt", DesiredAccess = GenericRead, CreateDisposition = 1 };
            Assert.AreEqual("base", File.ReadAllText((await otherHook.CallOkAsync(read, HookResolveReply.Read)).CachePath));

            HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
            File.WriteAllText(open.CachePath, "written by the first machine A");
            await s.CloseAsync(open.WriteToken);

            // The other Broker trusts its watched metadata for minutes: only the notification makes it see the new version.
            string seen = "";
            for (int i = 0; i < 200 && seen != "written by the first machine A"; i++)
            {
                seen = File.ReadAllText((await otherHook.CallOkAsync(read, HookResolveReply.Read)).CachePath);
                if (seen != "written by the first machine A")
                    await Task.Delay(50);
            }
            Assert.AreEqual("written by the first machine A", seen);
        }
        finally
        {
            await otherHook.DisposeAsync();
            await other.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task OwnWriteBackNotification_DoesNotReloadTheDirtyCopy()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());
        s.Share.WriteFile("b.txt", "sibling"u8.ToArray());
        await WaitUntilAsync(() => s.Broker.Cache.IsWatchActive, "the Broker watches the share");

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        await s.Broker.Cache.WaitForReadAheadAsync();
        Task notified = WaitForNotificationAsync(s.Broker.Cache, "a.txt");
        File.WriteAllText(open.CachePath, "v1");
        await s.FlushAsync(open.WriteToken);
        Assert.AreEqual("v1", File.ReadAllText(remote));
        await notified;

        // Still open, changed again: machine B has v1, the cache copy is dirty. The notification of our own flush cleared
        // the directory's read-ahead mark, so opening the sibling reads the directory ahead again, a.txt included.
        File.WriteAllText(open.CachePath, "v1 then v2");
        await s.ResolveAsync("b.txt", GenericRead, 1);
        await s.Broker.Cache.WaitForReadAheadAsync();
        Assert.AreEqual("v1 then v2", File.ReadAllText(open.CachePath), "read-ahead must not download over a dirty copy");
        HookResolveReply reader = await s.ResolveAsync("a.txt", GenericRead, 1);
        Assert.AreEqual("v1 then v2", File.ReadAllText(reader.CachePath));

        await s.CloseAsync(open.WriteToken);
        Assert.AreEqual("v1 then v2", File.ReadAllText(remote));
        Assert.IsEmpty(s.Broker.WriteBack.Conflicts);
    }

    [TestMethod]
    public async Task ChangeOnMachineBWhileDirty_InvalidatesButKeepsTheCopy_UntilTheConflictIsDecided()
    {
        await using Session s = await Session.StartAsync();
        string remote = s.Share.WriteFile("a.txt", "base"u8.ToArray());
        s.Share.WriteFile("b.txt", "sibling"u8.ToArray());
        await WaitUntilAsync(() => s.Broker.Cache.IsWatchActive, "the Broker watches the share");

        HookResolveReply open = await s.ResolveAsync("a.txt", GenericRead | GenericWrite, 1);
        await s.Broker.Cache.WaitForReadAheadAsync();
        File.WriteAllText(open.CachePath, "edited on machine A");
        Task notified = WaitForNotificationAsync(s.Broker.Cache, "a.txt");
        File.WriteAllText(remote, "edited on machine B, longer");
        await notified;

        // Metadata follows machine B at once; the dirty copy stays until close.
        HookStatReply stat = await s.Hook.CallOkAsync(new HookStatRequest { MappingIndex = 0, RelativePath = "a.txt" }, HookStatReply.Read);
        Assert.AreEqual((long)"edited on machine B, longer".Length, stat.Info.Size);
        await s.ResolveAsync("b.txt", GenericRead, 1);
        await s.Broker.Cache.WaitForReadAheadAsync();
        Assert.AreEqual("edited on machine A", File.ReadAllText(open.CachePath));

        await s.CloseAsync(open.WriteToken);
        Assert.AreEqual("edited on machine B, longer", File.ReadAllText(remote));
        WriteConflict conflict = s.Broker.WriteBack.Conflicts.Single();
        Assert.AreEqual("edited on machine A", File.ReadAllText(s.Share.Combine(conflict.ConflictPath)));
    }
}
