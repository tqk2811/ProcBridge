using System.Net.WebSockets;
using System.Text;

using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Relay.Shared.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel;

/// <summary>Control WebSocket of one host. Writes are serialized (several sessions may notify at once).</summary>
internal sealed class TunnelControlChannel
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TunnelControlChannel(WebSocket socket) => _socket = socket;

    /// <summary>
    /// Runs the Noise XX handshake as responder (the host is the initiator) and returns the host's static key, which
    /// the handshake authenticated: message 3 only decrypts for the owner of the private key, and the relay's fresh
    /// ephemeral key makes a replayed handshake fail. The host does not check the relay key (the relay is trusted with
    /// nothing); the relay key only gives the host something to prove itself to.
    /// </summary>
    /// <exception cref="InvalidDataException">The host broke the handshake (wrong message, bad key, wrong prologue).</exception>
    public async Task<byte[]> ProveHostKeyAsync(IdentityKey relayKey, CancellationToken cancellationToken)
    {
        using var handshake = NoiseHandshake.CreatePairing(isInitiator: false, relayKey, Encoding.ASCII.GetBytes(TunnelProtocol.ControlPrologue));
        try
        {
            handshake.ReadMessage(await ReceiveBinaryAsync(cancellationToken));
            byte[] reply = handshake.WriteMessage([]);
            await _socket.SendAsync(reply, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
            handshake.ReadMessage(await ReceiveBinaryAsync(cancellationToken));
        }
        catch (Exception e) when (e is CryptoProtocolException or System.Security.Cryptography.CryptographicException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Control handshake failed: {e.Message}", e);
        }
        if (!handshake.IsCompleted || handshake.RemoteStaticPublicKey is not { } hostKey)
            throw new InvalidDataException("Control handshake did not complete.");
        return hostKey;
    }

    private async Task<byte[]> ReceiveBinaryAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[NoiseHandshake.MaxMessageSize];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
                throw new InvalidDataException("Control handshake message is too large.");
            var result = await _socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("Expected a binary handshake message.");
            length += result.Count;
            if (result.EndOfMessage)
                return buffer.AsSpan(0, length).ToArray();
        }
    }

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

    /// <summary>Most bytes of one text message from the host: its messages are tiny JSON lines.</summary>
    public const int MaxHostMessageBytes = 4096;

    /// <summary>
    /// Reads incoming frames until the host closes and answers the close handshake. Each whole text message goes to
    /// <paramref name="onText"/> (binary frames are ignored); a message over <see cref="MaxHostMessageBytes"/> closes the
    /// channel.
    /// </summary>
    public async Task RunAsync(Func<string, Task> onText, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHostMessageBytes];
        int length = 0;
        while (_socket.State == WebSocketState.Open)
        {
            if (length == buffer.Length)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "control message too large", cancellationToken);
                return;
            }
            var result = await _socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                return;
            }
            length += result.Count;
            if (!result.EndOfMessage)
                continue;
            if (result.MessageType == WebSocketMessageType.Text)
                await onText(Encoding.UTF8.GetString(buffer, 0, length));
            length = 0;
        }
    }

    public void Abort() => _socket.Abort();
}
