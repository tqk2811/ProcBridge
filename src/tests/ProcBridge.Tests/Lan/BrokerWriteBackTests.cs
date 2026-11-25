using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Tests.Lan;

/// <summary>
/// M3 Broker side played by <see cref="FakeHook"/>: creating on machine B, write tokens, write-back on close, conflicts,
/// the persistent queue, rename, delete and attribute changes (plan 4.5 and 10).
/// </summary>
[TestClass]
public sealed class BrokerWriteBackTests
{
    private const string VirtualRoot = @"V:\Remote\Proj";
    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;

    private sealed class Session : IAsyncDisposable
    {
        private Session(TempFolder share, TempFolder work, AgentServer agent, BrokerHost broker, FakeHook hook)
        {
            Share = share;
            Work = work;
            Agent = agent;
            Broker = broker;
            Hook = hook;
        }

        public TempFolder Share { get; }
        public TempFolder Work { get; }
        public AgentServer Agent { get; private set; }
        public BrokerHost Broker { get; private set; }
        public FakeHook Hook { get; private set; }

        public static async Task<Session> StartAsync(ConflictPolicy policy = ConflictPolicy.KeepBoth)
        {
            var share = new TempFolder("wshare");
            var work = new TempFolder("wwork");
            AgentServer agent = StartAgent(share, 0);
            (BrokerHost broker, FakeHook hook) = await StartBrokerAsync(work, agent.Port, policy);
            return new Session(share, work, agent, broker, hook);
        }

        public static AgentServer StartAgent(TempFolder share, int port)
        {
            var agent = new AgentServer([new AgentShare("src", share.Path, writable: true)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = port });
            agent.Start();
            return agent;
        }

        public static async Task<(BrokerHost, FakeHook)> StartBrokerAsync(TempFolder work, int port, ConflictPolicy policy = ConflictPolicy.KeepBoth)
        {
            string session = Guid.NewGuid().ToString("N");
            BrokerHost broker = BrokerHost.Start(new BrokerOptions
            {
                SessionId = session, AgentHost = "127.0.0.1", AgentPort = port, ShareName = "src",
                Mappings = [new VirtualMapping(VirtualRoot, "")],
                CacheRoot = Path.Combine(work.Path, "cache"),
                WriteQueueRoot = Path.Combine(work.Path, "queue"),
                ConflictPolicy = policy,
                WriteRetryDelay = TimeSpan.FromMilliseconds(200),
                Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(2), ConnectBackoff = TimeSpan.Zero },
            });
            FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
            await hook.CallOkAsync(new HookHelloRequest { ProcessId = 1, Architecture = HookArchitecture.X64 }, HookHelloReply.Read);
            return (broker, hook);
        }

        public async Task RestartBrokerAsync(int port)
        {
            await Hook.DisposeAsync();
            await Broker.DisposeAsync();
            (Broker, Hook) = await StartBrokerAsync(Work, port);
        }

        public async Task StopAgentAsync() => await Agent.DisposeAsync();

        public void StartAgentAgain(int port) => Agent = StartAgent(Share, port);

        public Task<HookResolveReply> ResolveAsync(string path, uint access, uint disposition, uint options = 0)
            => Hook.CallOkAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = path, DesiredAccess = access, CreateDisposition = disposition, CreateOptions = options }, HookResolveReply.Read);

        public async Task CloseAsync(ulong token)
            => await Hook.CallOkAsync(new HookTokenRequest { Type = HookIpcMessageType.Close, WriteToken = token }, (ref HookIpcReader _) => 0);

        public async ValueTask DisposeAsync()
        {
            await Hook.DisposeAsync();
            await Broker.DisposeAsync();
            await Agent.DisposeAsync();
            Work.Dispose();
            Share.Dispose();
        }
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
}
