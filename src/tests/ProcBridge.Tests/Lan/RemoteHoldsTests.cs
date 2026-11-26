using System.Net;
using System.Reflection;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>M8: handles machine B holds for writers on machine A (who holds them, and what a broken hold connection does).</summary>
[TestClass]
public sealed class RemoteHoldsTests
{
    private const uint ShareNone = 0;
    private const uint ShareReadWrite = 0x3;

    private static readonly TransportOptions Transport = new() { ConnectionCount = 2, ConnectTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(30) };

    private static AgentServer StartAgent(TempFolder share)
    {
        var server = new AgentServer([new AgentShare("src", share.Path, writable: true)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        return server;
    }

    private static AgentClient CreateClient(int port) => new(new TcpConnectionFactory("127.0.0.1", port, Transport), "src", Transport);

    private static async Task<NtStatus> StatusOfAsync(Func<Task> action)
    {
        try
        {
            await action();
            return NtStatus.Success;
        }
        catch (ProtocolStatusException e)
        {
            return e.Status;
        }
    }

    private static bool CanOpenLocally(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Breaks the dedicated connection the holds live on, like a network failure.</summary>
    private static async Task BreakHoldConnectionAsync(RemoteHolds holds)
    {
        var connection = (ProtocolConnection)typeof(RemoteHolds).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(holds)!;
        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Hold_StaysUntilItsTokenEnds()
    {
        // Which processes hold the token is the write-back queue's business: the hold goes when it says the token ended.
        using var share = new TempFolder("hold");
        share.WriteFile("a.txt", "data"u8.ToArray());
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        await using var holds = new RemoteHolds(client, holdShareMode: true, TimeSpan.Zero);

        await holds.OpenAsync(1, "a.txt", ShareReadWrite, CancellationToken.None);
        await holds.ReleaseAsync(2);
        Assert.AreEqual(1, holds.Count, "another token ending leaves this one");
        await holds.ReleaseAsync(1);
        Assert.AreEqual(0, holds.Count);
        await holds.ReleaseAsync(1);
    }

    [TestMethod]
    public async Task BrokenHoldConnection_ReportsTheLostLocks_AndHoldsTheShareModeAgain()
    {
        using var share = new TempFolder("hold");
        string file = share.WriteFile("a.txt", "data"u8.ToArray());
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        await using var holds = new RemoteHolds(client, holdShareMode: true, TimeSpan.Zero);
        await holds.OpenAsync(1, "a.txt", ShareNone, CancellationToken.None);
        await holds.LockAsync(1, 0, 4, exclusive: true, failImmediately: true, CancellationToken.None);
        Assert.IsFalse(CanOpenLocally(file));

        await BreakHoldConnectionAsync(holds);

        // The share mode is held again on a new connection.
        bool refused = false;
        for (int i = 0; i < 200 && !refused; i++)
        {
            refused = !CanOpenLocally(file) && holds.Count == 1;
            if (!refused)
                await Task.Delay(25);
        }
        Assert.IsTrue(refused, "A user of machine B can open the file the writer on machine A holds without sharing.");
        // The process believes it still has its lock: it must learn that it is gone, not get a fresh one silently.
        Assert.AreEqual(NtStatus.FileLockConflict, await StatusOfAsync(() => holds.LockAsync(1, 8, 4, exclusive: true, failImmediately: true, CancellationToken.None)));
        Assert.AreEqual(NtStatus.FileLockConflict, await StatusOfAsync(() => holds.UnlockAsync(1, 0, 4, CancellationToken.None)));
    }
}
