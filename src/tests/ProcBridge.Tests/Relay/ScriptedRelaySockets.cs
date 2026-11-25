using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ProcBridge.Transport.Relay.Interfaces;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// <see cref="IRelaySocketFactory"/> whose sockets are scripted by the test: the control channel gets the frames the
/// test pushes, lanes block until closed. Records every connect (URI and bearer) so a test can check what the host sends.
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
        var socket = new ScriptedSocket();
        lock (_lock)
        {
            _connects.Add((uri, bearerToken));
            if (uri.AbsolutePath.EndsWith("/control", StringComparison.Ordinal))
                _controls.Add(socket);
        }
        return Task.FromResult<WebSocket>(socket);
    }

    /// <summary>Waits until the control channel (the <paramref name="index"/>th connect to it) exists.</summary>
    public async Task<ScriptedSocket> ControlAsync(int index = 0)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            lock (_lock)
            {
                if (_controls.Count > index)
                    return _controls[index];
            }
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The host never connected its control channel.");
            await Task.Delay(10);
        }
    }

    internal sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<(byte[] Data, bool End)> _incoming = Channel.CreateUnbounded<(byte[], bool)>();
        private WebSocketState _state = WebSocketState.Open;
        private byte[] _pending = [];
        private bool _pendingEnd;
        private int _pendingOffset;

        public WebSocketCloseStatus? ClosedWith { get; private set; }

        public bool Aborted { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void PushText(string json) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(json), true));

        /// <summary>One frame of a message that is not finished yet.</summary>
        public void PushPartial(int size) => _incoming.Writer.TryWrite((new byte[size], false));

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

        public override void Dispose() => _incoming.Writer.TryComplete();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_pendingOffset >= _pending.Length)
            {
                try
                {
                    (_pending, _pendingEnd) = await _incoming.Reader.ReadAsync(cancellationToken);
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
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
