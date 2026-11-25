using ProcBridge.Crypto.Sessions;
using ProcBridge.Protocol.Interfaces;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// <see cref="IFrameProtector"/> over one <see cref="SecureChannel"/>: every frame body is sealed with ChaCha20-Poly1305
/// under the channel's frame counter. <c>StreamFrameChannel</c> calls <see cref="Protect"/> under its send lock and
/// writes the result before releasing it, so frames reach the wire in counter order (the guarantee
/// <see cref="SecureChannel.SealAndWriteAsync"/> gives other writers). Owns the channel.
/// </summary>
public sealed class SecureFrameProtector : IFrameProtector, IDisposable
{
    private readonly SecureChannel _channel;

    public SecureFrameProtector(SecureChannel channel)
    {
        _channel = channel;
    }

    public uint ChannelId => _channel.Id;

    public int Overhead => SecureChannel.Overhead;

    public ReadOnlyMemory<byte> Protect(ReadOnlyMemory<byte> body) => _channel.Seal(body.Span);

    /// <exception cref="ProcBridge.Crypto.CryptoProtocolException">Forged, corrupted, replayed or reordered frame.</exception>
    public ReadOnlyMemory<byte> Unprotect(ReadOnlyMemory<byte> data) => _channel.Open(data.Span);

    public void Dispose() => _channel.Dispose();
}
