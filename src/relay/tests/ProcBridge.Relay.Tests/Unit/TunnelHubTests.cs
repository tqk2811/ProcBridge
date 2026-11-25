using System.Net.WebSockets;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel;
using ProcBridge.Relay.Services.Tunnel.Models;

namespace ProcBridge.Relay.Tests.Unit;

[TestClass]
public sealed class TunnelHubTests
{
    private static TunnelHub NewHub() => new(Options.Create(new TunnelOptions()), TimeProvider.System, NullLogger<TunnelHub>.Instance);

    [TestMethod]
    public async Task MáyChủOfflineThìMởPhiênBáoDeviceOffline()
    {
        var hub = NewHub();
        var ex = await Assert.ThrowsExactlyAsync<ServiceException>(
            () => hub.OpenSessionAsync(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), null, CancellationToken.None));
        Assert.AreEqual(RelayErrors.DeviceOffline, ex.Code);
        Assert.AreEqual(409, ex.StatusCode);
    }

    [TestMethod]
    public void PhiênKhôngTồnTạiThìKhôngGiữĐượcLane()
    {
        var ex = Assert.ThrowsExactly<ServiceException>(() => NewHub().ReserveLane(Guid.NewGuid(), "pbt_x", 0));
        Assert.AreEqual(RelayErrors.TunnelSessionNotFound, ex.Code);
    }

    [TestMethod]
    public async Task VéSaiLaneNgoàiDảiVàLaneBậnĐềuBịTừChối()
    {
        var hub = NewHub();
        var hostId = Guid.NewGuid();
        using var control = new ControlSocketStub();
        var controlTask = hub.RunControlChannelAsync(hostId, control, CancellationToken.None);
        Assert.IsTrue(hub.IsOnline(hostId));

        var session = await hub.OpenSessionAsync(Guid.NewGuid(), null, Guid.NewGuid(), hostId, null, CancellationToken.None);
        Assert.HasCount(1, control.Sent);

        Assert.AreEqual(RelayErrors.TunnelSessionNotFound, Assert.ThrowsExactly<ServiceException>(() => hub.ReserveLane(session.SessionId, "pbt_sai", 0)).Code);
        Assert.AreEqual(RelayErrors.TunnelLaneInvalid, Assert.ThrowsExactly<ServiceException>(() => hub.ReserveLane(session.SessionId, session.Ticket, 99)).Code);

        var first = hub.ReserveLane(session.SessionId, session.Ticket, 0);
        Assert.AreEqual(RelayErrors.TunnelLaneBusy, Assert.ThrowsExactly<ServiceException>(() => hub.ReserveLane(session.SessionId, session.Ticket, 0)).Code);
        hub.ReleaseLane(first);
        hub.ReleaseLane(hub.ReserveLane(session.SessionId, session.Ticket, 0));

        control.Close();
        await controlTask;
        Assert.IsFalse(hub.IsOnline(hostId));
    }

    [TestMethod]
    public async Task ThuHồiXenGiữaKiểmQuyềnVàThêmPhiênThìPhiênBịGỡVàMáyChủKhôngĐượcBáo()
    {
        var hub = NewHub();
        var hostId = Guid.NewGuid();
        var shareId = Guid.NewGuid();
        using var control = new ControlSocketStub();
        var controlTask = hub.RunControlChannelAsync(hostId, control, CancellationToken.None);

        var epoch = hub.RevocationEpoch;       // the caller read the epoch, then checked the grant: still valid ...
        hub.DisconnectShare(shareId);          // ... and the revocation committed and cut the share right here.
        var access = new TunnelAccessCheck(epoch, _ => Task.FromResult(false));

        var ex = await Assert.ThrowsExactlyAsync<ServiceException>(
            () => hub.OpenSessionAsync(Guid.NewGuid(), null, shareId, hostId, null, CancellationToken.None, access));
        Assert.AreEqual(RelayErrors.ShareAccessDenied, ex.Code);
        Assert.IsEmpty(control.Sent, "Máy chủ không được báo về phiên đã bị thu hồi.");

        control.Close();
        await controlTask;
    }

    [TestMethod]
    public async Task KhôngCóThuHồiThìKhôngKiểmLạiQuyền()
    {
        var hub = NewHub();
        var hostId = Guid.NewGuid();
        using var control = new ControlSocketStub();
        var controlTask = hub.RunControlChannelAsync(hostId, control, CancellationToken.None);

        var rechecks = 0;
        var access = new TunnelAccessCheck(hub.RevocationEpoch, _ =>
        {
            rechecks++;
            return Task.FromResult(false);
        });
        var session = await hub.OpenSessionAsync(Guid.NewGuid(), null, Guid.NewGuid(), hostId, null, CancellationToken.None, access);
        Assert.AreEqual(0, rechecks);
        hub.ReleaseLane(hub.ReserveLane(session.SessionId, session.Ticket, 0));

        // A revocation of something else moves the epoch: the check runs, and a still-valid grant is kept.
        var moved = new TunnelAccessCheck(hub.RevocationEpoch, _ => Task.FromResult(true));
        hub.DisconnectShare(Guid.NewGuid());
        var kept = await hub.OpenSessionAsync(Guid.NewGuid(), null, Guid.NewGuid(), hostId, null, CancellationToken.None, moved);
        hub.ReleaseLane(hub.ReserveLane(kept.SessionId, kept.Ticket, 0));

        control.Close();
        await controlTask;
    }

    [TestMethod]
    public async Task ThuHồiMáyXenGiữaXácThựcVàĐăngKýKênhĐiềuKhiểnThìKênhBịĐóng()
    {
        var hub = NewHub();
        var hostId = Guid.NewGuid();
        using var control = new ControlSocketStub();

        var epoch = hub.RevocationEpoch;   // read before the device token was checked
        hub.DisconnectDevice(hostId);      // revoked right after that check, before the channel registered
        var access = new TunnelAccessCheck(epoch, _ => Task.FromResult(false));

        await hub.RunControlChannelAsync(hostId, control, CancellationToken.None, access).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(hub.IsOnline(hostId));
    }

    [TestMethod]
    public async Task PhiênBịThuHồiKhôngNhậnThêmLaneKểCảKhiĐãTraCứuTrước()
    {
        var hub = NewHub();
        var hostId = Guid.NewGuid();
        var shareId = Guid.NewGuid();
        using var control = new ControlSocketStub();
        var controlTask = hub.RunControlChannelAsync(hostId, control, CancellationToken.None);
        var session = await hub.OpenSessionAsync(Guid.NewGuid(), null, shareId, hostId, null, CancellationToken.None);

        hub.DisconnectShare(shareId);
        Assert.AreEqual(RelayErrors.TunnelSessionNotFound, Assert.ThrowsExactly<ServiceException>(() => hub.ReserveLane(session.SessionId, session.Ticket, 0)).Code);

        control.Close();
        await controlTask;
    }

    /// <summary>Control socket that records sent frames and blocks reads until closed.</summary>
    private sealed class ControlSocketStub : WebSocket
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public List<string> Sent { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void Close() => _closed.TrySetResult();

        public override void Abort() => _closed.TrySetResult();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() => _closed.TrySetResult();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add(System.Text.Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }
}
