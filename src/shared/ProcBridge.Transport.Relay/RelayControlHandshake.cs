using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Transport.Relay;

/// <summary>
/// Machine B side of the control channel handshake (plan 8.2): a Noise XX handshake as initiator proves to the relay
/// that this machine owns <c>identity</c>; the relay then registers the channel under HostId = fingerprint of that key.
/// The relay's own key is not checked: the relay is trusted with nothing, the end-to-end Noise IK in the lanes is.
/// </summary>
internal static class RelayControlHandshake
{
    /// <returns>The HostId the relay registered (it must equal <see cref="IdentityKey.Fingerprint"/>).</returns>
    /// <exception cref="InvalidDataException">The relay broke the handshake or registered another HostId.</exception>
    public static async Task<string> RunAsync(WebSocket control, IdentityKey identity, int maxMessageBytes, CancellationToken cancellationToken)
    {
        using NoiseHandshake handshake = NoiseHandshake.CreatePairing(isInitiator: true, identity, Encoding.ASCII.GetBytes(TunnelProtocol.ControlPrologue));
        try
        {
            await control.SendAsync(handshake.WriteMessage([]), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            (WebSocketMessageType type, byte[] reply) = await ReceiveAsync(control, NoiseHandshake.MaxMessageSize, cancellationToken).ConfigureAwait(false);
            if (type != WebSocketMessageType.Binary)
                throw new InvalidDataException("Relay did not answer the control handshake.");
            handshake.ReadMessage(reply);
            await control.SendAsync(handshake.WriteMessage([]), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ProcBridge.Crypto.CryptoProtocolException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new InvalidDataException($"Control handshake with the relay failed: {e.Message}", e);
        }

        (WebSocketMessageType kind, byte[] text) = await ReceiveAsync(control, maxMessageBytes, cancellationToken).ConfigureAwait(false);
        if (kind != WebSocketMessageType.Text)
            throw new InvalidDataException(kind == WebSocketMessageType.Close
                ? $"Relay refused the control channel: {control.CloseStatusDescription}"
                : "Relay did not confirm the registration.");
        JObject json = JObject.Parse(Encoding.UTF8.GetString(text));
        if (json.Value<string>("type") != TunnelProtocol.HostRegisteredMessage)
            throw new InvalidDataException("Relay did not confirm the registration.");
        TunnelHostRegisteredMessageVM registered = json.ToObject<TunnelHostRegisteredMessageVM>(JsonSerializer.Create(RelayJsonSettings.Create()))!;
        if (!string.Equals(registered.HostId, identity.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException($"Relay registered HostId {registered.HostId}, expected {identity.Fingerprint}.");
        return registered.HostId;
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
        using var message = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, []);
            if (message.Length + result.Count > maxBytes)
                throw new InvalidDataException($"Relay control message is larger than {maxBytes} bytes.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, message.ToArray());
        }
    }
}
