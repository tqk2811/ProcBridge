using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Transport.Relay.Interfaces;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// <see cref="IRelaySocketFactory"/> whose sockets are scripted by the test: the control channel answers the host's
/// Noise handshake like the relay (then <c>hostRegistered</c>) and afterwards gets the frames the test pushes; lanes
/// block until closed. Records every connect (URI and bearer) so a test can check what the host sends.
/// </summary>
internal sealed class ScriptedRelaySockets : IRelaySocketFactory
{
    private readonly object _lock = new();
    private readonly List<(Uri Uri, string? Bearer)> _connects = [];
    private readonly List<ScriptedSocket> _controls = [];

    public IReadOnlyList<(Uri Uri, string? Bearer)> Connects
    {
        get
        {
            lock (_lock)
                return [.. _connects];
        }
    }

    public IReadOnlyList<ScriptedSocket> Controls
    {
        get
        {
            lock (_lock)
                return [.. _controls];
        }
    }

    public Task<WebSocket> ConnectAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken)
    {
        bool control = uri.AbsolutePath.EndsWith("/control", StringComparison.Ordinal);
        var socket = new ScriptedSocket(control);
        lock (_lock)
        {
            _connects.Add((uri, bearerToken));
            if (control)
                _controls.Add(socket);
        }
        return Task.FromResult<WebSocket>(socket);
    }

    /// <summary>Waits until the control channel (the <paramref name="index"/>th connect to it) exists and its handshake is done.</summary>
    public async Task<ScriptedSocket> ControlAsync(int index = 0)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            lock (_lock)
            {
                if (_controls.Count > index && _controls[index].Registered)
                    return _controls[index];
            }
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The host never connected and proved its control channel.");
            await Task.Delay(10);
        }
    }

    internal sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<(byte[] Data, bool End, WebSocketMessageType Type)> _incoming = Channel.CreateUnbounded<(byte[], bool, WebSocketMessageType)>();
        private readonly IdentityKey? _relayKey;
        private readonly NoiseHandshake? _handshake;
        private WebSocketState _state = WebSocketState.Open;
        private byte[] _pending = [];
        private bool _pendingEnd;
        private WebSocketMessageType _pendingType;
        private int _pendingOffset;

        public ScriptedSocket(bool control)
        {
            if (!control)
                return;
            _relayKey = IdentityKey.Generate();
            _handshake = NoiseHandshake.CreatePairing(isInitiator: false, _relayKey, Encoding.ASCII.GetBytes(TunnelProtocol.ControlPrologue));
        }

        /// <summary>The handshake finished and <c>hostRegistered</c> was queued: frames pushed now come after it.</summary>
        public bool Registered { get; private set; }

        /// <summary>HostId the scripted relay registered (fingerprint of the key the host proved).</summary>
        public string? HostId { get; private set; }

        public WebSocketCloseStatus? ClosedWith { get; private set; }

        private readonly List<string> _sentTexts = [];

        /// <summary>Text messages the host sent after the handshake (pairing slot requests), in order.</summary>
        public IReadOnlyList<string> SentTexts
        {
            get
            {
                lock (_sentTexts)
                    return [.. _sentTexts];
            }
        }

        public bool Aborted { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void PushText(string json) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(json), true, WebSocketMessageType.Text));

        /// <summary>One frame of a message that is not finished yet.</summary>
        public void PushPartial(int size) => _incoming.Writer.TryWrite((new byte[size], false, WebSocketMessageType.Text));

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = closeStatus;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _incoming.Writer.TryComplete();
            _handshake?.Dispose();
            _relayKey?.Dispose();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_pendingOffset >= _pending.Length)
            {
                try
                {
                    (_pending, _pendingEnd, _pendingType) = await _incoming.Reader.ReadAsync(cancellationToken);
                    _pendingOffset = 0;
                }
                catch (ChannelClosedException)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                }
            }
            int count = Math.Min(buffer.Count, _pending.Length - _pendingOffset);
            Array.Copy(_pending, _pendingOffset, buffer.Array!, buffer.Offset, count);
            _pendingOffset += count;
            bool end = _pendingOffset >= _pending.Length && _pendingEnd;
            return new WebSocketReceiveResult(count, _pendingType, end);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // The host's handshake messages (whole messages: the host sends each in one frame).
            if (_handshake is not null && messageType == WebSocketMessageType.Binary && !Registered)
            {
                _handshake.ReadMessage(buffer.AsSpan());
                if (!_handshake.IsCompleted)
                {
                    _incoming.Writer.TryWrite((_handshake.WriteMessage([]), true, WebSocketMessageType.Binary));
                }
                else
                {
                    HostId = KeyFingerprint.Compute(_handshake.RemoteStaticPublicKey!);
                    PushText($$"""{"type":"{{TunnelProtocol.HostRegisteredMessage}}","hostId":"{{HostId}}"}""");
                    Registered = true;
                }
            }
            else if (messageType == WebSocketMessageType.Text)
            {
                lock (_sentTexts)
                    _sentTexts.Add(Encoding.UTF8.GetString(buffer.AsSpan()));
            }
            return Task.CompletedTask;
        }
    }
}
