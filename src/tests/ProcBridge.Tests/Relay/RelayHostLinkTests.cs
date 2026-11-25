using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Transport.Relay;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Tests.Relay;

/// <summary>Host side of the relay tunnel against a scripted control channel: what it remembers, what it refuses, what it sends.</summary>
[TestClass]
public sealed class RelayHostLinkTests
{
    private static readonly RelayEndpoint Endpoint = new(new Uri("http://relay.test/"), "pbd_device");
    private static readonly DateTime Now = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string Opened(Guid session, DateTime expiresAt, string ticket = "pbt_host")
        => $$"""{"type":"{{TunnelProtocol.SessionOpenedMessage}}","sessionId":"{{session}}","ticket":"{{ticket}}","expiresAt":"{{expiresAt:O}}","shareId":"{{Guid.NewGuid()}}","clientDeviceId":"{{Guid.NewGuid()}}","clientPublicKey":null}""";

    private static string LaneRequested(Guid session, int lane)
        => $$"""{"type":"{{TunnelProtocol.LaneRequestedMessage}}","sessionId":"{{session}}","lane":{{lane}}}""";

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out: " + what);
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task ExpiredSessionsWithoutLanes_AreForgotten_OnTheNextMessage()
    {
        DateTime now = Now;
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, (_, _) => Task.CompletedTask, sockets) { UtcNow = () => now, SessionSweepInterval = TimeSpan.FromHours(1) };
        link.Start();
        var control = await sockets.ControlAsync();

        for (int i = 0; i < 5; i++)
            control.PushText(Opened(Guid.NewGuid(), Now.AddSeconds(30)));
        await WaitAsync(() => link.TrackedSessionCount == 5, "five sessions tracked");

        now = Now.AddHours(1);
        control.PushText(Opened(Guid.NewGuid(), now.AddSeconds(30)));
        await WaitAsync(() => link.TrackedSessionCount == 1, "expired sessions swept when a new one arrives");
    }

    [TestMethod]
    public async Task ExpiredSessions_AreForgotten_ByTheTimer()
    {
        DateTime now = Now;
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, (_, _) => Task.CompletedTask, sockets) { UtcNow = () => now, SessionSweepInterval = TimeSpan.FromMilliseconds(20) };
        link.Start();
        var control = await sockets.ControlAsync();
        control.PushText(Opened(Guid.NewGuid(), Now.AddSeconds(30)));
        await WaitAsync(() => link.TrackedSessionCount == 1, "session tracked");

        now = Now.AddHours(1);
        await WaitAsync(() => link.TrackedSessionCount == 0, "timer swept the expired session");
    }

    [TestMethod]
    public async Task SessionCount_IsCapped_OldestFirst()
    {
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, (_, _) => Task.CompletedTask, sockets)
        {
            UtcNow = () => Now,
            SessionSweepInterval = TimeSpan.FromHours(1),
            MaxTrackedSessions = 3,
        };
        link.Start();
        var control = await sockets.ControlAsync();

        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        for (int i = 0; i < ids.Length; i++)
            control.PushText(Opened(ids[i], Now.AddMinutes(1 + i)));
        await WaitAsync(() => link.TrackedSessionCount == 3, "cap enforced");

        // The newest three survive: a lane on the oldest is no longer served, a lane on the newest is.
        control.PushText(LaneRequested(ids[0], 0));
        control.PushText(LaneRequested(ids[7], 0));
        await WaitAsync(() => sockets.Connects.Any(x => x.Uri.Query.Contains(ids[7].ToString())), "lane of the newest session");
        Assert.IsFalse(sockets.Connects.Any(x => x.Uri.Query.Contains(ids[0].ToString())));
    }

    [TestMethod]
    public async Task SessionWithAnOpenLane_IsKept_AndKeptARetentionAfterItCloses()
    {
        DateTime now = Now;
        var laneRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLane = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, async (_, _) =>
        {
            laneRunning.TrySetResult();
            await releaseLane.Task;
        }, sockets)
        {
            UtcNow = () => now,
            SessionSweepInterval = TimeSpan.FromHours(1),
            SessionRetention = TimeSpan.FromMinutes(2),
        };
        link.Start();
        var control = await sockets.ControlAsync();
        var session = Guid.NewGuid();
        control.PushText(Opened(session, Now.AddSeconds(30)));
        control.PushText(LaneRequested(session, 0));
        await laneRunning.Task.WaitAsync(TimeSpan.FromSeconds(15));

        now = Now.AddHours(1);
        link.SweepSessions();
        Assert.AreEqual(1, link.TrackedSessionCount, "A session with an open lane is not forgotten.");

        releaseLane.SetResult();
        // Once the lane has ended the session is still kept for the retention (the relay accepts lanes a while longer) ...
        now = now.AddMinutes(1);
        await Task.Delay(300);
        link.SweepSessions();
        Assert.AreEqual(1, link.TrackedSessionCount, "Kept for the retention after the last lane closed.");

        // ... and forgotten after it.
        now = now.AddMinutes(5);
        await WaitAsync(() => { link.SweepSessions(); return link.TrackedSessionCount == 0; }, "forgotten after the retention");
    }

    [TestMethod]
    public async Task OversizedControlMessage_ClosesTheChannel_AndTheHostReconnects()
    {
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, (_, _) => Task.CompletedTask, sockets)
        {
            MaxControlMessageBytes = 64 * 1024,
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        };
        link.Start();
        var first = await sockets.ControlAsync();

        // 80 frames of 1 KB that never end a message: more than 64 KB buffered.
        for (int i = 0; i < 80; i++)
            first.PushPartial(1024);

        await WaitAsync(() => first.Aborted, "oversized message drops the channel");
        Assert.AreEqual(System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig, first.ClosedWith);
        StringAssert.Contains(link.LastError ?? string.Empty, "larger than");
        await sockets.ControlAsync(1);
    }

    [TestMethod]
    public async Task Lane_CarriesTheTicketInTheAuthorizationHeader_NotTheUrl()
    {
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, async (_, _) => await Task.Delay(50), sockets);
        link.Start();
        var control = await sockets.ControlAsync();
        var session = Guid.NewGuid();
        control.PushText(Opened(session, DateTime.UtcNow.AddMinutes(1), "pbt_secret-ticket"));
        control.PushText(LaneRequested(session, 3));

        await WaitAsync(() => sockets.Connects.Count >= 2, "lane connected");
        var (uri, bearer) = sockets.Connects[1];
        Assert.AreEqual("pbt_secret-ticket", bearer);
        Assert.DoesNotContain("pbt_secret-ticket", uri.ToString());
        Assert.DoesNotContain("ticket", uri.Query);
        StringAssert.Contains(uri.Query, "lane=3");
    }
}
