using System.Net.WebSockets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Tunnel;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// The real relay host (REST + WebSocket tunnel) in this test process. It keeps no accounts and no storage: a host is
/// online while its proven control channel is open. WebSockets go through <see cref="Sockets"/>, which also records
/// every byte that crosses the relay on data lanes.
/// </summary>
internal sealed class InProcessRelay : IAsyncDisposable
{
    private InProcessRelay()
    {
        Factory = new RelayApiFactory();
        Sockets = new RecordingSocketFactory(Factory.Server);
    }

    public RelayApiFactory Factory { get; }

    public RecordingSocketFactory Sockets { get; }

    public static InProcessRelay Start() => new();

    public RelayEndpoint Endpoint => new(Factory.Server.BaseAddress);

    public IRelayApi Api() => new RelayApiImplement(Factory.CreateClient());

    /// <summary>Waits until a host with <paramref name="hostId"/> has a proven control channel.</summary>
    public async Task WaitOnlineAsync(string hostId)
    {
        TunnelHub hub = Factory.Services.GetRequiredService<TunnelHub>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!hub.IsOnline(hostId))
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The Agent did not come online on the relay.");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
    }
}

/// <summary>
/// WebSockets to the in-process test server. Data lanes (path /tunnel) are wrapped so every byte sent or received
/// on them is copied to <see cref="CapturedLaneBytes"/>: that is exactly what the relay forwards.
/// </summary>
internal sealed class RecordingSocketFactory(TestServer server) : IRelaySocketFactory
{
    private readonly object _lock = new();
    private readonly MemoryStream _captured = new();

    public int LaneCount { get; private set; }

    public byte[] CapturedLaneBytes
    {
        get
        {
            lock (_lock)
                return _captured.ToArray();
        }
    }

    public async Task<WebSocket> ConnectAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken)
    {
        WebSocketClient client = server.CreateWebSocketClient();
        if (bearerToken is not null)
            client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + bearerToken;
        WebSocket socket = await client.ConnectAsync(uri, cancellationToken);
        if (!IsDataLane(uri))
            return socket;
        lock (_lock)
            LaneCount++;
        return new RecordingWebSocket(socket, Capture);
    }

    private static bool IsDataLane(Uri uri) => string.Equals(uri.AbsolutePath, TunnelProtocol.DataPath, StringComparison.Ordinal);

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
            _captured.Write(bytes);
    }

    private sealed class RecordingWebSocket(WebSocket inner, SpanAction capture) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;

        public override void Abort() => inner.Abort();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => inner.Dispose();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            WebSocketReceiveResult result = await inner.ReceiveAsync(buffer, cancellationToken);
            capture(buffer.AsSpan(0, result.Count));
            return result;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            capture(buffer.AsSpan());
            return inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }
    }

    private delegate void SpanAction(ReadOnlySpan<byte> bytes);
}
