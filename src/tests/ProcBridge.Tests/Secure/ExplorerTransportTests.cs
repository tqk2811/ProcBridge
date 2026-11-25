using System.Net;
using System.Security.Cryptography;
using ProcBridge.Agent;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Models;
using ProcBridge.Explorer.Client;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Tests.Secure;

/// <summary>Remote Explorer over the encrypted transport: the Agent applies the profile it keeps for the paired machine A.</summary>
[TestClass]
public sealed class ExplorerTransportTests
{
    private static readonly TransportOptions Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10), MaxRetries = 0 };

    private sealed class Setup : IAsyncDisposable
    {
        private readonly SecureConnectionFactory _factory;

        public Setup(TempFolder share, ExplorerPolicy? policy, IExplorerAuditLog? audit = null)
        {
            if (policy is not null)
                Policies.Set(Peers.BrokerKey.Fingerprint, policy);
            Server = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 },
                Peers.Agent() with { ExplorerPolicies = Policies, ExplorerAudit = audit });
            Server.Start();
            _factory = new SecureConnectionFactory(new TcpStreamConnector("127.0.0.1", Server.Port), Peers.BrokerKey, Peers.BrokerTrust, Peers.AgentKey.PublicKey, Transport);
            Client = new ExplorerClient(_factory, Transport);
        }

        public SecurePeers Peers { get; } = new();
        public ExplorerPolicyStore Policies { get; } = new();
        public AgentServer Server { get; }
        public ExplorerClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _factory.Dispose();
            await Server.DisposeAsync();
            Peers.Dispose();
        }
    }

    [TestMethod]
    public async Task FileRead_WithinTheRoot_ListsAndDownloadsInRawChunks()
    {
        using var root = new TempFolder("exp");
        byte[] content = RandomNumberGenerator.GetBytes(600 * 1024 + 17);
        root.WriteFile(@"dir\big.bin", content);
        await using var s = new Setup(root, new ExplorerPolicy { FileRoots = [root.Path], Permissions = ExplorerPermissions.FileRead });

        DirectoryListResponse list = await s.Client.ListDirectoryAsync(root.Path, CancellationToken.None);
        Assert.AreEqual(ExplorerStatus.Ok, list.Status, list.Error);
        Assert.IsTrue(list.Entries.Any(x => x.Name == "dir" && x.IsDirectory));

        FileStatResponse stat = await s.Client.StatAsync(root.Combine(@"dir\big.bin"), CancellationToken.None);
        Assert.AreEqual(content.Length, stat.Entry!.Size);

        using var copy = new MemoryStream();
        Assert.AreEqual(ExplorerStatus.Ok, await s.Client.DownloadAsync(root.Combine(@"dir\big.bin"), copy, CancellationToken.None));
        CollectionAssert.AreEqual(content, copy.ToArray());
    }

    [TestMethod]
    public async Task UnknownMachineProfile_AllowsNothing()
    {
        using var root = new TempFolder("exp");
        root.WriteFile("a.txt", [1]);
        await using var s = new Setup(root, policy: null);

        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.ListDirectoryAsync(root.Path, CancellationToken.None)).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.ReadFileAsync(root.Combine("a.txt"), 0, 10, CancellationToken.None)).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.ListProcessesAsync(CancellationToken.None)).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.ListServicesAsync(CancellationToken.None)).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.GetSystemInfoAsync(CancellationToken.None)).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.ListRegistryAsync(@"HKCU\Software", CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Profile_LimitsKindsRootsAndWrites()
    {
        using var root = new TempFolder("exp");
        using var outside = new TempFolder("exp-out");
        root.WriteFile("keep.txt", [1]);
        outside.WriteFile("secret.txt", [2]);
        var audit = new ListAudit();
        await using var s = new Setup(root, new ExplorerPolicy { FileRoots = [root.Path], Permissions = ExplorerPermissions.FileRead | ExplorerPermissions.SystemInfo }, audit);

        SystemInfoResponse info = await s.Client.GetSystemInfoAsync(CancellationToken.None);
        Assert.AreEqual(ExplorerStatus.Ok, info.Status, info.Error);
        Assert.AreEqual(Environment.MachineName, info.MachineName);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.ListProcessesAsync(CancellationToken.None)).Status);
        Assert.AreEqual(ExplorerStatus.OutOfScope, (await s.Client.ReadFileAsync(outside.Combine("secret.txt"), 0, 10, CancellationToken.None)).Status);

        ExplorerAckResponse delete = await s.Client.DeleteAsync(root.Combine("keep.txt"), recursive: false, CancellationToken.None);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, delete.Status);
        Assert.IsTrue(File.Exists(root.Combine("keep.txt")));
        // The refused write is logged with the authenticated machine A.
        ExplorerAuditEntry entry = audit.Entries.Single();
        Assert.AreEqual(s.Peers.BrokerKey.Fingerprint, entry.RequesterId);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, entry.Status);
    }

    [TestMethod]
    public async Task Policy_ReportsTheProfileOfThisMachine_AndNothingWithoutOne()
    {
        using var root = new TempFolder("exp");
        await using var s = new Setup(root, new ExplorerPolicy { FileRoots = [root.Path], RegistryRoots = [@"HKCU\Software"], Permissions = ExplorerPermissions.FileRead | ExplorerPermissions.ProcessKill });

        ExplorerPolicyResponse policy = await s.Client.GetPolicyAsync(CancellationToken.None);
        Assert.AreEqual(ExplorerStatus.Ok, policy.Status, policy.Error);
        Assert.AreEqual(ExplorerPermissions.FileRead | ExplorerPermissions.ProcessKill, policy.Permissions);
        CollectionAssert.AreEqual(new[] { root.Path }, policy.FileRoots.ToArray());
        CollectionAssert.AreEqual(new[] { @"HKCU\Software" }, policy.RegistryRoots.ToArray());

        s.Policies.Set(s.Peers.BrokerKey.Fingerprint, new ExplorerPolicy());
        await using var fresh = new ExplorerClient(new SecureConnectionFactory(new TcpStreamConnector("127.0.0.1", s.Server.Port), s.Peers.BrokerKey, s.Peers.BrokerTrust, s.Peers.AgentKey.PublicKey, Transport), Transport);
        ExplorerPolicyResponse none = await fresh.GetPolicyAsync(CancellationToken.None);
        Assert.AreEqual(ExplorerPermissions.None, none.Permissions);
        Assert.IsEmpty(none.FileRoots);
    }

    [TestMethod]
    public async Task ProfileChange_AppliesToNewConnections()
    {
        using var root = new TempFolder("exp");
        root.WriteFile("a.txt", [1, 2, 3]);
        await using var s = new Setup(root, policy: null);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await s.Client.StatAsync(root.Combine("a.txt"), CancellationToken.None)).Status);

        s.Policies.Set(s.Peers.BrokerKey.Fingerprint, new ExplorerPolicy { FileRoots = [root.Path], Permissions = ExplorerPermissions.FileRead });
        await using var fresh = new ExplorerClient(new SecureConnectionFactory(new TcpStreamConnector("127.0.0.1", s.Server.Port), s.Peers.BrokerKey, s.Peers.BrokerTrust, s.Peers.AgentKey.PublicKey, Transport), Transport);
        FileReadResponse read = await fresh.ReadFileAsync(root.Combine("a.txt"), 0, 10, CancellationToken.None);
        Assert.AreEqual(ExplorerStatus.Ok, read.Status, read.Error);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, read.Data);
    }

    [TestMethod]
    public async Task PlainTransport_RefusesTheExplorer()
    {
        using var root = new TempFolder("exp");
        await using var server = new AgentServer([new ReadOnlyShare("src", root.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, AllowPlaintextTransport = true });
        server.Start();
        await using var client = new ExplorerClient(new TcpConnectionFactory("127.0.0.1", server.Port, Transport), Transport);

        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.GetSystemInfoAsync(CancellationToken.None));
        Assert.AreEqual(NtStatus.AccessDenied, e.Status);
    }

    [TestMethod]
    public void PolicyStore_DefaultsToNothing_AndPersists()
    {
        using var folder = new TempFolder("exp-store");
        string path = folder.Combine("policies.bin");
        var protector = new ProcBridge.Crypto.Identity.DpapiKeyProtector();
        var store = new ExplorerPolicyStore(path, protector);
        Assert.AreEqual(ExplorerPermissions.None, store.Get("nobody").Permissions);
        Assert.IsEmpty(store.Get("nobody").FileRoots);

        store.Set("fp-a", new ExplorerPolicy { FileRoots = [@"C:\data"], Permissions = ExplorerPermissions.FileRead | ExplorerPermissions.ProcessRead });
        ExplorerPolicy loaded = new ExplorerPolicyStore(path, protector).Get("fp-a");
        Assert.AreEqual(ExplorerPermissions.FileRead | ExplorerPermissions.ProcessRead, loaded.Permissions);
        CollectionAssert.AreEqual(new[] { @"C:\data" }, loaded.FileRoots.ToArray());
        Assert.IsTrue(store.Remove("fp-a"));
        Assert.AreEqual(ExplorerPermissions.None, new ExplorerPolicyStore(path, protector).Get("fp-a").Permissions);
    }

    private sealed class ListAudit : IExplorerAuditLog
    {
        private readonly List<ExplorerAuditEntry> _entries = [];

        public IReadOnlyList<ExplorerAuditEntry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public void Write(ExplorerAuditEntry entry)
        {
            lock (_entries)
                _entries.Add(entry);
        }
    }
}
