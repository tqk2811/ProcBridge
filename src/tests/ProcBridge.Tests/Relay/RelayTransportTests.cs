using ProcBridge.Crypto.Identity;
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
        var https = new RelayEndpoint(new Uri("https://relay.example/base/"));
        var http = new RelayEndpoint(new Uri("http://localhost:5290"));

        Assert.AreEqual("wss://relay.example/base/tunnel?sessionId=1&lane=0", https.WebSocketUri("/tunnel?sessionId=1&lane=0").ToString());
        Assert.AreEqual("ws://localhost:5290/tunnel/control", http.WebSocketUri("/tunnel/control").ToString());
    }

    [TestMethod]
    public async Task Lanes_ShareOneSession_UntilItIsFull()
    {
        await using var relay = InProcessRelay.Start();
        using var agentKey = IdentityKey.Generate();
        await using var host = new RelayHostLink(relay.Endpoint, agentKey, EchoAsync, relay.Sockets);
        host.Start();
        await relay.WaitOnlineAsync(agentKey.Fingerprint);
        using var connector = new RelayStreamConnector(relay.Endpoint, agentKey.Fingerprint, relay.Sockets, relay.Factory.CreateClient());

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
    public async Task HostOfflineOrUnknown_IsConnectionFailure()
    {
        await using var relay = InProcessRelay.Start();
        using var agentKey = IdentityKey.Generate();
        using var offline = new RelayStreamConnector(relay.Endpoint, agentKey.Fingerprint, relay.Sockets, relay.Factory.CreateClient());
        await Assert.ThrowsExactlyAsync<IOException>(() => offline.ConnectAsync(CancellationToken.None).AsTask());

        // Another host online does not answer for this HostId.
        using var otherKey = IdentityKey.Generate();
        await using var other = new RelayHostLink(relay.Endpoint, otherKey, EchoAsync, relay.Sockets);
        other.Start();
        await relay.WaitOnlineAsync(otherKey.Fingerprint);
        await Assert.ThrowsExactlyAsync<IOException>(() => offline.ConnectAsync(CancellationToken.None).AsTask());
    }

    [TestMethod]
    public void CommandLine_Relay_IsParsed()
    {
        LauncherOptions options = LauncherCommandLine.Parse(
            ["--relay", "https://relay.example/", "--agent-fingerprint", "AAAA",
             "--share", "src", "--map", @"V:\a", "", "--", "cmd.exe"]);

        Assert.IsNotNull(options.Relay);
        Assert.AreEqual(new Uri("https://relay.example/"), options.Relay.Url);
        Assert.AreEqual("relay.example", options.AgentHost);
        Assert.AreEqual("AAAA", options.AgentFingerprint);
    }

    [TestMethod]
    [DataRow("--agent-fingerprint")]
    [DataRow("--plaintext")]
    [DataRow("--relay-token")]
    public void CommandLine_Relay_RefusesIncompletePlaintextOrOldOptions(string change)
    {
        var args = new List<string> { "--relay", "https://relay.example/", "--agent-fingerprint", "AAAA" };
        int at = args.IndexOf(change);
        if (at >= 0)
            args.RemoveRange(at, 2);
        else if (change == "--relay-token")
            args.AddRange([change, "pbd_x"]);
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
