using ProcBridge.Crypto.Sessions;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// Frame channel of one secure connection: <see cref="StreamFrameChannel"/> with a <see cref="SecureFrameProtector"/>,
/// plus the identity of the authenticated peer. Owns the stream and the <see cref="SecureChannel"/>.
/// </summary>
public sealed class SecureFrameChannel : IFrameChannel
{
    private readonly StreamFrameChannel _inner;
    private readonly SecureFrameProtector _protector;
    private readonly Action? _onDisposed;
    private int _disposed;

    /// <param name="onDisposed">Called once after dispose (the server counts the live channels of a session).</param>
    internal SecureFrameChannel(Stream stream, SecureChannel channel, int maxFrameSize, string peerFingerprint, Action? onDisposed = null)
    {
        _onDisposed = onDisposed;
        _protector = new SecureFrameProtector(channel);
        _inner = new StreamFrameChannel(stream, ownsStream: true, maxFrameSize, _protector);
        PeerFingerprint = peerFingerprint;
    }

    /// <summary>Fingerprint of the peer's identity key, authenticated by the Noise handshake.</summary>
    public string PeerFingerprint { get; }

    public uint ChannelId => _protector.ChannelId;

    public ValueTask SendAsync(Frame frame, CancellationToken cancellationToken) => _inner.SendAsync(frame, cancellationToken);

    public ValueTask<Frame?> ReceiveAsync(CancellationToken cancellationToken) => _inner.ReceiveAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _inner.DisposeAsync().ConfigureAwait(false);
        _protector.Dispose();
        _onDisposed?.Invoke();
    }
}
