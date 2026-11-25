using System.Net.WebSockets;
using ProcBridge.Transport.Relay.Interfaces;

namespace ProcBridge.Transport.Relay;

/// <summary><see cref="IRelaySocketFactory"/> over <see cref="ClientWebSocket"/> (TLS by the system for <c>wss</c>).</summary>
public sealed class ClientWebSocketFactory : IRelaySocketFactory
{
    /// <summary>Keep-alive pings so NATs and proxies do not drop an idle control channel or lane.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<WebSocket> ConnectAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        if (bearerToken is not null)
            socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
