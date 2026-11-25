using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Crypto.Identity;
using ProcBridge.Explorer.Client;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Models;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Tests.Secure;

/// <summary>The paired machine list and the Explorer profiles are files that other instances or processes may change.</summary>
[TestClass]
public sealed class SharedStoreTests
{
    private static readonly TransportOptions Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10), MaxRetries = 0 };

    [TestMethod]
    public void PairedMachineStore_ReloadsWhenAnotherInstanceRevokes_AndRaisesRevoked()
    {
        using var folder = new TempFolder("shared-store");
        string path = folder.Combine("paired.bin");
        var protector = new DpapiKeyProtector();
        using var key = IdentityKey.Generate();
        var first = new PairedMachineStore(path, protector);
        first.Add(key.PublicKey, "machine-b");
        var second = new PairedMachineStore(path, protector);
        Assert.IsTrue(first.IsTrusted(key.PublicKey));

        string? revoked = null;
        first.Revoked += x => revoked = x.Fingerprint;
        Assert.IsTrue(second.Revoke(key.Fingerprint));

        Assert.IsFalse(first.IsTrusted(key.PublicKey));
        Assert.AreEqual(key.Fingerprint, revoked);
        Assert.IsNull(first.Find(key.Fingerprint));
    }

    [TestMethod]
    public void PairedMachineStore_AddAfterAnotherInstanceRevoked_DoesNotRestoreTheRevokedMachine()
    {
        using var folder = new TempFolder("shared-store");
        string path = folder.Combine("paired.bin");
        var protector = new DpapiKeyProtector();
        using var revokedKey = IdentityKey.Generate();
        using var otherKey = IdentityKey.Generate();
        var first = new PairedMachineStore(path, protector);
        first.Add(revokedKey.PublicKey, "revoked");
        var second = new PairedMachineStore(path, protector);

        second.Revoke(revokedKey.Fingerprint);
        // The first instance still holds the revoked machine in memory; saving must not write it back.
        first.Add(otherKey.PublicKey, "other");

        var reloaded = new PairedMachineStore(path, protector);
        Assert.AreEqual(otherKey.Fingerprint, reloaded.List().Single().Fingerprint);
        Assert.IsFalse(first.IsTrusted(revokedKey.PublicKey));
    }

    [TestMethod]
    public async Task PairedMachineStore_ConcurrentAddsFromTwoInstances_KeepEveryMachine()
    {
        using var folder = new TempFolder("shared-store");
        string path = folder.Combine("paired.bin");
        var protector = new DpapiKeyProtector();
        var first = new PairedMachineStore(path, protector);
        var second = new PairedMachineStore(path, protector);
        IdentityKey[] keys = Enumerable.Range(0, 10).Select(_ => IdentityKey.Generate()).ToArray();
        try
        {
            await Task.WhenAll(keys.Select((key, i) => Task.Run(() => (i % 2 == 0 ? first : second).Add(key.PublicKey, $"m{i}"))));
            Assert.HasCount(keys.Length, new PairedMachineStore(path, protector).List());
        }
        finally
        {
            foreach (IdentityKey key in keys)
                key.Dispose();
        }
    }

    [TestMethod]
    public void ExplorerPolicyStore_ReloadsWhenAnotherInstanceChanges_AndRaisesChanged()
    {
        using var folder = new TempFolder("shared-store");
        string path = folder.Combine("policies.bin");
        var protector = new DpapiKeyProtector();
        var first = new ExplorerPolicyStore(path, protector);
        first.Set("fp-a", new ExplorerPolicy { FileRoots = [@"C:\data"], Permissions = ExplorerPermissions.FileRead });
        var second = new ExplorerPolicyStore(path, protector);
        var changed = new List<string>();
        first.Changed += changed.Add;

        second.Set("fp-a", new ExplorerPolicy());
        second.Set("fp-b", new ExplorerPolicy { Permissions = ExplorerPermissions.SystemInfo });

        Assert.AreEqual(ExplorerPermissions.None, first.Get("fp-a").Permissions);
        Assert.AreEqual(ExplorerPermissions.SystemInfo, first.Get("fp-b").Permissions);
        CollectionAssert.AreEquivalent(new[] { "fp-a", "fp-b" }, changed.Distinct().ToArray());

        // A stale instance does not bring the old profile back when it saves another one.
        first.Set("fp-c", new ExplorerPolicy());
        Assert.AreEqual(ExplorerPermissions.None, new ExplorerPolicyStore(path, protector).Get("fp-a").Permissions);
        Assert.AreEqual(ExplorerPermissions.SystemInfo, new ExplorerPolicyStore(path, protector).Get("fp-b").Permissions);
    }

    [TestMethod]
    public async Task Agent_DropsTheConnectionOfAMachineRevokedByAnotherInstance()
    {
        using var share = new TempFolder("shared-agent");
        share.WriteFile("a.txt", [1]);
        using var folder = new TempFolder("shared-agent-store");
        var protector = new DpapiKeyProtector();
        using var agentKey = IdentityKey.Generate();
        using var brokerKey = IdentityKey.Generate();
        string path = folder.Combine("paired.bin");
        var agentTrust = new PairedMachineStore(path, protector);
        agentTrust.Add(brokerKey.PublicKey, "machine-a");
        var brokerTrust = new PairedMachineStore();
        brokerTrust.Add(agentKey.PublicKey, "machine-b");
        await using var server = new AgentServer([new ReadOnlyShare("src", share.Path)],
            new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, TrustRefreshInterval = TimeSpan.FromMilliseconds(50) },
            new AgentSecurity { Identity = agentKey, PairedMachines = agentTrust });
        server.Start();
        using var factory = new SecureConnectionFactory(new TcpStreamConnector("127.0.0.1", server.Port), brokerKey, brokerTrust, agentKey.PublicKey, Transport);
        await using var client = new AgentClient(factory, "src", Transport);
        Assert.AreEqual(1L, (await client.StatAsync("a.txt", CancellationToken.None)).Size);
        Assert.AreEqual(1, server.ActiveSessionCount);

        // Another instance (in production another process, a settings tool) revokes the machine; this Agent never saw the call.
        new PairedMachineStore(path, protector).Revoke(brokerKey.Fingerprint);

        await WaitUntilAsync(() => server.ActiveSessionCount == 0);
        await Assert.ThrowsAsync<ProtocolStatusException>(() => client.StatAsync("a.txt", CancellationToken.None));
    }

    [TestMethod]
    public async Task Agent_DropsTheConnectionOfAMachineWhoseProfileAnotherInstanceChanged()
    {
        using var share = new TempFolder("shared-agent");
        using var folder = new TempFolder("shared-agent-store");
        var protector = new DpapiKeyProtector();
        using var agentKey = IdentityKey.Generate();
        using var brokerKey = IdentityKey.Generate();
        string path = folder.Combine("policies.bin");
        var policies = new ExplorerPolicyStore(path, protector);
        policies.Set(brokerKey.Fingerprint, new ExplorerPolicy { FileRoots = [share.Path], Permissions = ExplorerPermissions.FileRead });
        var agentTrust = new PairedMachineStore();
        agentTrust.Add(brokerKey.PublicKey, "machine-a");
        var brokerTrust = new PairedMachineStore();
        brokerTrust.Add(agentKey.PublicKey, "machine-b");
        await using var server = new AgentServer([new ReadOnlyShare("src", share.Path)],
            new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, TrustRefreshInterval = TimeSpan.FromMilliseconds(50) },
            new AgentSecurity { Identity = agentKey, PairedMachines = agentTrust, ExplorerPolicies = policies });
        server.Start();
        using var factory = new SecureConnectionFactory(new TcpStreamConnector("127.0.0.1", server.Port), brokerKey, brokerTrust, agentKey.PublicKey, Transport);
        await using var client = new ExplorerClient(factory, Transport);
        Assert.AreEqual(ExplorerStatus.Ok, (await client.ListDirectoryAsync(share.Path, CancellationToken.None)).Status);

        new ExplorerPolicyStore(path, protector).Set(brokerKey.Fingerprint, new ExplorerPolicy());

        await WaitUntilAsync(() => server.ActiveSessionCount == 0);
        await using var fresh = new ExplorerClient(factory, Transport);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, (await fresh.ListDirectoryAsync(share.Path, CancellationToken.None)).Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not reached in time.");
            await Task.Delay(20);
        }
    }
}
