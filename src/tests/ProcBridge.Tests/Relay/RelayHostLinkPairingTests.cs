using Newtonsoft.Json.Linq;
using ProcBridge.Crypto.Identity;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Transport.Relay;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// Pairing slot of the host link against a scripted relay: which slot messages it sends, in what order, and how it reacts
/// to late, stale or refused answers and to the relay's slot lifetime.
/// </summary>
[TestClass]
public sealed class RelayHostLinkPairingTests
{
    private static readonly RelayEndpoint Endpoint = new(new Uri("http://relay.test/"));
    private static readonly IdentityKey Identity = IdentityKey.Generate();
    private static readonly DateTime Start = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string State(string type, string key, double? ttl = null, DateTime? expiresAt = null)
        => new JObject
        {
            ["type"] = type,
            ["lookupKey"] = key,
            ["expiresAt"] = expiresAt?.ToString("O"),
            ["expiresInSeconds"] = ttl,
        }.ToString(Newtonsoft.Json.Formatting.None);

    /// <summary>(type, lookupKey) of every text message the host sent.</summary>
    private static List<(string? Type, string? Key)> Sent(ScriptedRelaySockets.ScriptedSocket control)
        => control.SentTexts.Select(JObject.Parse).Select(x => (x.Value<string>("type"), x.Value<string>("lookupKey"))).ToList();

    private static async Task<List<(string? Type, string? Key)>> WaitSentAsync(ScriptedRelaySockets.ScriptedSocket control, int count, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var sent = Sent(control);
            if (sent.Count >= count)
                return sent;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out: {what} (sent {string.Join(", ", sent.Select(x => x.Type))})");
            await Task.Delay(10);
        }
    }

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
    public async Task RegisteredForAStaleKey_PutsTheWantedSlotBack()
    {
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, Identity, (_, _) => Task.CompletedTask, sockets);
        link.SetPairingSlot(true);
        link.Start();
        var control = await sockets.ControlAsync();
        string wanted = (await WaitSentAsync(control, 1, "first slot request"))[0].Key!;

        // The relay says it holds some older key (a reordered or late answer): the link must ask for its slot again.
        string stale = RelayPairingCode.DeriveLookupKey("00000000");
        control.PushText(State(TunnelProtocol.PairingSlotRegisteredMessage, stale, ttl: 300));
        var sent = await WaitSentAsync(control, 2, "slot asked for again");
        Assert.AreEqual((TunnelProtocol.PairingSlotMessage, wanted), sent[1]);
        Assert.IsNull(link.PairingCode, "A code is shown only for the key the link asked for.");

        control.PushText(State(TunnelProtocol.PairingSlotRegisteredMessage, wanted, ttl: 300));
        await WaitAsync(() => link.PairingCode is not null, "code shown");
    }

    [TestMethod]
    public async Task LateRegistered_AfterPairingWasTurnedOff_IsCancelledAgain()
    {
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, Identity, (_, _) => Task.CompletedTask, sockets);
        link.SetPairingSlot(true);
        link.Start();
        var control = await sockets.ControlAsync();
        string key = (await WaitSentAsync(control, 1, "slot request"))[0].Key!;

        link.SetPairingSlot(false);
        var sent = await WaitSentAsync(control, 2, "cancel");
        Assert.AreEqual(TunnelProtocol.PairingSlotCancelMessage, sent[1].Type);

        // The answer to the first request arrives after the cancel: the relay now holds a slot nobody wants.
        control.PushText(State(TunnelProtocol.PairingSlotRegisteredMessage, key, ttl: 300));
        sent = await WaitSentAsync(control, 3, "second cancel");
        Assert.AreEqual(TunnelProtocol.PairingSlotCancelMessage, sent[2].Type);
        Assert.IsNull(link.PairingCode);
    }

    [TestMethod]
    public async Task SlotExpiry_FollowsTheRelayLifetime_OnThisMachinesClock()
    {
        DateTime now = Start;
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, Identity, (_, _) => Task.CompletedTask, sockets)
        {
            UtcNow = () => now,
            PairingCheckInterval = TimeSpan.FromMilliseconds(10),
        };
        link.SetPairingSlot(true);
        link.Start();
        var control = await sockets.ControlAsync();
        string key = (await WaitSentAsync(control, 1, "slot request"))[0].Key!;

        // The relay's clock is an hour behind: its expiresAt is already past here, but the slot lives 300 s from now.
        control.PushText(State(TunnelProtocol.PairingSlotRegisteredMessage, key, ttl: 300, expiresAt: Start.AddHours(-1)));
        await WaitAsync(() => link.PairingCode is not null, "code shown");
        await Task.Delay(200);
        Assert.HasCount(1, Sent(control), "No new code while the slot still lives.");
        Assert.AreEqual(Start.AddSeconds(300), link.PairingCodeExpiresAt);

        // A few seconds before the end a new code is asked for.
        now = Start.AddSeconds(296);
        var sent = await WaitSentAsync(control, 2, "renewal before expiry");
        Assert.AreEqual(TunnelProtocol.PairingSlotMessage, sent[1].Type);
        Assert.AreNotEqual(key, sent[1].Key);
    }

    [TestMethod]
    public async Task TooManyRejections_ShowAnError_AndRetryLater()
    {
        var sockets = new ScriptedRelaySockets();
        await using var link = new RelayHostLink(Endpoint, Identity, (_, _) => Task.CompletedTask, sockets)
        {
            MaxPairingRejections = 2,
            PairingRetryDelay = TimeSpan.FromMilliseconds(100),
            PairingCheckInterval = TimeSpan.FromMilliseconds(10),
        };
        link.SetPairingSlot(true);
        link.Start();
        var control = await sockets.ControlAsync();

        // The relay refuses every code.
        for (int i = 1; i <= 3; i++)
        {
            var sent = await WaitSentAsync(control, i, $"slot request {i}");
            control.PushText(State(TunnelProtocol.PairingSlotRejectedMessage, sent[i - 1].Key!));
        }
        await WaitAsync(() => link.PairingError is not null, "error shown after too many rejections");
        Assert.IsNull(link.PairingCode);

        // It tries again after the delay instead of giving up for good, and a success clears the error.
        var retry = await WaitSentAsync(control, 4, "retry after the delay");
        control.PushText(State(TunnelProtocol.PairingSlotRegisteredMessage, retry[3].Key!, ttl: 300));
        await WaitAsync(() => link.PairingCode is not null && link.PairingError is null, "code shown, error cleared");
    }
}
