using System.Net.WebSockets;
using System.Text;

namespace ProcBridge.Relay.Services.Tunnel;

/// <summary>Control WebSocket of one host device. Writes are serialized (several sessions may notify at once).</summary>
internal sealed class TunnelControlChannel
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TunnelControlChannel(WebSocket socket) => _socket = socket;

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Reads (and ignores) incoming frames until the device closes; answers the close handshake.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (_socket.State == WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                return;
            }
        }
    }

    public void Abort() => _socket.Abort();
}
