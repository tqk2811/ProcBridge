using ProcBridge.Launcher;
using ProcBridge.Launcher.Models;
using ProcBridge.Transport.Relay;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// Relay tunnel transport without the hook: <see cref="RelayStreamConnector"/> lanes against <see cref="RelayHostLink"/>
/// through an in-process relay, plus the relay options of the Launcher command line.
/// </summary>
[TestClass]
public sealed class RelayTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    public void Endpoint_BuildsWebSocketUrls()
    {
        var https = new RelayEndpoint(new Uri("https://relay.example/base/"), "pbd_x");
        var http = new RelayEndpoint(new Uri("http://localhost:5290"), "pbd_x");

        Assert.AreEqual("wss://relay.example/base/tunnel?sessionId=1&lane=0", https.WebSocketUri("/tunnel?sessionId=1&lane=0").ToString());
        Assert.AreEqual("ws://localhost:5290/tunnel/control", http.WebSocketUri("/tunnel/control").ToString());
        Assert.DoesNotContain("pbd_x", https.ToString());
    }

    [TestMethod]
    public async Task Lanes_ShareOneSession_UntilItIsFull()
    {
        await using var relay = InProcessRelay.Start();
        RelayTopology topology = await relay.CreateTopologyAsync("echo");
        await using var host = new RelayHostLink(relay.Endpoint(topology.Host.Token), EchoAsync, relay.Sockets);
        host.Start();
        await relay.WaitOnlineAsync(topology.OwnerToken, topology.Host.Device.Id);
        using var connector = new RelayStreamConnector(relay.Endpoint(topology.Client.Token), topology.ShareId, relay.Sockets, relay.Factory.CreateClient());

        // Three lanes in parallel, each its own echo.
        Stream[] lanes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => connector.ConnectAsync(CancellationToken.None).AsTask()));
        for (int i = 0; i < lanes.Length; i++)
            await AssertEchoAsync(lanes[i], (byte)i);
        Assert.AreEqual(1, connector.SessionCount);

        // Up to 16 lanes per session; the 17th opens a second session.
        var more = new List<Stream>();
        for (int i = 3; i < 17; i++)
            more.Add(await connector.ConnectAsync(CancellationToken.None));
        await AssertEchoAsync(more[^1], 17);
        Assert.AreEqual(2, connector.SessionCount);

        // A lane given back is reused by the next connection, on the first session.
        await lanes[1].DisposeAsync();
        Stream reused = await connector.ConnectAsync(CancellationToken.None);
        await AssertEchoAsync(reused, 42);
        Assert.AreEqual(2, connector.SessionCount);

        foreach (Stream lane in lanes.Concat(more).Append(reused))
            await lane.DisposeAsync();
    }

    [TestMethod]
    public async Task ShareNotGranted_IsAccessDenied_AgentOffline_IsConnectionFailure()
    {
        await using var relay = InProcessRelay.Start();
        RelayTopology topology = await relay.CreateTopologyAsync("echo");
        using var offline = new RelayStreamConnector(relay.Endpoint(topology.Client.Token), topology.ShareId, relay.Sockets, relay.Factory.CreateClient());
        await Assert.ThrowsExactlyAsync<IOException>(() => offline.ConnectAsync(CancellationToken.None).AsTask());

        await using var host = new RelayHostLink(relay.Endpoint(topology.Host.Token), EchoAsync, relay.Sockets);
        host.Start();
        await relay.WaitOnlineAsync(topology.OwnerToken, topology.Host.Device.Id);
        using var stranger = new RelayStreamConnector(relay.Endpoint(topology.Client.Token), Guid.NewGuid(), relay.Sockets, relay.Factory.CreateClient());
        var denied = await Assert.ThrowsExactlyAsync<ProcBridge.Protocol.ProtocolStatusException>(() => stranger.ConnectAsync(CancellationToken.None).AsTask());
        Assert.AreEqual(ProcBridge.Protocol.Enums.NtStatus.AccessDenied, denied.Status);
    }

    [TestMethod]
    public async Task RevokingTheGrant_CutsOpenLanes()
    {
        await using var relay = InProcessRelay.Start();
        RelayTopology topology = await relay.CreateTopologyAsync("echo");
        await using var host = new RelayHostLink(relay.Endpoint(topology.Host.Token), EchoAsync, relay.Sockets);
        host.Start();
        await relay.WaitOnlineAsync(topology.OwnerToken, topology.Host.Device.Id);
        using var connector = new RelayStreamConnector(relay.Endpoint(topology.Client.Token), topology.ShareId, relay.Sockets, relay.Factory.CreateClient());
        await using Stream lane = await connector.ConnectAsync(CancellationToken.None);
        await AssertEchoAsync(lane, 1);

        await relay.Api(topology.OwnerToken).Shares.RevokeGrantAsync(topology.ShareId, topology.Client.Device.Id);

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            Assert.AreEqual(0, await lane.ReadAsync(new byte[16], timeout.Token));
        }
        catch (IOException)
        {
            // Aborted rather than closed: also cut.
        }
    }

    [TestMethod]
    public void CommandLine_Relay_IsParsed()
    {
        Guid share = Guid.NewGuid();
        LauncherOptions options = LauncherCommandLine.Parse(
            ["--relay", "https://relay.example/", "--relay-token", "pbd_x", "--relay-share", share.ToString(), "--agent-fingerprint", "AAAA",
             "--share", "src", "--map", @"V:\a", "", "--", "cmd.exe"]);

        Assert.IsNotNull(options.Relay);
        Assert.AreEqual(share, options.Relay.ShareId);
        Assert.AreEqual("pbd_x", options.Relay.DeviceToken);
        Assert.AreEqual("relay.example", options.AgentHost);
        Assert.AreEqual("AAAA", options.AgentFingerprint);
    }

    [TestMethod]
    [DataRow("--relay-share")]
    [DataRow("--agent-fingerprint")]
    [DataRow("--plaintext")]
    public void CommandLine_Relay_RefusesIncompleteOrPlaintext(string change)
    {
        var args = new List<string> { "--relay", "https://relay.example/", "--relay-token", "pbd_x", "--relay-share", Guid.NewGuid().ToString(), "--agent-fingerprint", "AAAA" };
        int at = args.IndexOf(change);
        if (at >= 0)
            args.RemoveRange(at, 2);
        else
            args.Add(change);
        args.AddRange(["--share", "src", "--map", @"V:\a", "", "--", "cmd.exe"]);

        Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(args));
    }

    /// <summary>Host side of the test: echoes every byte back until the lane closes.</summary>
    private static Task EchoAsync(Stream lane, string remote)
        => Task.Run(async () =>
        {
            await using (lane)
            {
                byte[] buffer = new byte[4096];
                int read;
                try
                {
                    while ((read = await lane.ReadAsync(buffer)) > 0)
                        await lane.WriteAsync(buffer.AsMemory(0, read));
                }
                catch (IOException)
                {
                }
            }
        });

    private static async Task AssertEchoAsync(Stream lane, byte marker)
    {
        byte[] sent = [marker, 1, 2, 3, (byte)(marker ^ 0xFF)];
        using var timeout = new CancellationTokenSource(Timeout);
        await lane.WriteAsync(sent, timeout.Token);
        byte[] received = new byte[sent.Length];
        await lane.ReadExactlyAsync(received, timeout.Token);
        CollectionAssert.AreEqual(sent, received);
    }
}
