using System.Buffers.Binary;
using System.Security.Cryptography;
using ProcBridge.Crypto.Identity;

namespace ProcBridge.Crypto.Sessions;

/// <summary>
/// Result of a completed Noise handshake. One session can carry many parallel connections
/// (TCP or WebSocket); each connection gets its own <see cref="SecureChannel"/> with keys derived
/// from the session keys and the channel id, so nonces never repeat across connections.
/// <para>
/// Channel ids: the initiator allocates even ids, the responder odd ids, so both sides can open
/// channels without coordination. The opener sends the id to the peer in clear (for example in
/// the first bytes of the new connection); the peer calls <see cref="AcceptChannel"/>. A given
/// id can be opened or accepted only once per session.
/// </para>
/// Thread-safe. Dispose wipes the session keys; channels already created stay usable.
/// </summary>
public sealed class SecureSession : IDisposable
{
    private static readonly byte[] _channelLabel = "ProcBridge channel v1"u8.ToArray();

    private readonly Lock _lock = new();
    private readonly HashSet<uint> _usedChannelIds = [];
    private readonly byte[] _initiatorToResponder;
    private readonly byte[] _responderToInitiator;
    private readonly byte[] _handshakeHash;
    private readonly byte[] _remoteStatic;
    private uint _nextLocalChannelId;
    private bool _disposed;

    internal SecureSession(bool isInitiator, byte[] initiatorToResponder, byte[] responderToInitiator, byte[] handshakeHash, byte[] remoteStatic)
    {
        IsInitiator = isInitiator;
        _initiatorToResponder = (byte[])initiatorToResponder.Clone();
        _responderToInitiator = (byte[])responderToInitiator.Clone();
        _handshakeHash = (byte[])handshakeHash.Clone();
        _remoteStatic = (byte[])remoteStatic.Clone();
        _nextLocalChannelId = isInitiator ? 0u : 1u;
        RemoteFingerprint = KeyFingerprint.Compute(_remoteStatic);
    }

    public bool IsInitiator { get; }

    /// <summary>Peer identity public key authenticated by the handshake.</summary>
    public ReadOnlySpan<byte> RemoteStaticPublicKey => _remoteStatic;

    public string RemoteFingerprint { get; }

    /// <summary>Noise handshake hash: identical on both sides, unique per session (channel binding).</summary>
    public ReadOnlySpan<byte> HandshakeHash => _handshakeHash;

    /// <summary>Frames sealed per channel before its keys are ratcheted with Noise REKEY. Internal for tests.</summary>
    internal ulong RekeyInterval { get; set; } = SecureChannel.DefaultRekeyInterval;

    /// <summary>Opens a new channel with a fresh local id. Send <see cref="SecureChannel.Id"/> to the peer.</summary>
    public SecureChannel OpenChannel()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_nextLocalChannelId > uint.MaxValue - 2)
                throw new InvalidOperationException("Channel ids exhausted; start a new session.");
            var id = _nextLocalChannelId;
            _nextLocalChannelId += 2;
            return CreateChannelLocked(id);
        }
    }

    /// <summary>Accepts a channel the peer opened.</summary>
    /// <exception cref="CryptoProtocolException">Id has the wrong parity or was already used (replayed connection).</exception>
    public SecureChannel AcceptChannel(uint channelId)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var peerParity = IsInitiator ? 1u : 0u;
            if ((channelId & 1u) != peerParity)
                throw new CryptoProtocolException($"Channel id {channelId} does not belong to the peer.");
            return CreateChannelLocked(channelId);
        }
    }

    private SecureChannel CreateChannelLocked(uint id)
    {
        if (!_usedChannelIds.Add(id))
            throw new CryptoProtocolException($"Channel id {id} was already used in this session.");
        var i2r = DeriveChannelKey(_initiatorToResponder, id);
        var r2i = DeriveChannelKey(_responderToInitiator, id);
        try
        {
            return IsInitiator
                ? new SecureChannel(id, sendKey: i2r, receiveKey: r2i, RekeyInterval)
                : new SecureChannel(id, sendKey: r2i, receiveKey: i2r, RekeyInterval);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(i2r);
            CryptographicOperations.ZeroMemory(r2i);
        }
    }

    private byte[] DeriveChannelKey(byte[] directionKey, uint channelId)
    {
        Span<byte> info = stackalloc byte[_channelLabel.Length + sizeof(uint)];
        _channelLabel.CopyTo(info);
        BinaryPrimitives.WriteUInt32BigEndian(info[_channelLabel.Length..], channelId);
        var key = new byte[SecureChannel.KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, directionKey, key, _handshakeHash, info);
        return key;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_initiatorToResponder);
            CryptographicOperations.ZeroMemory(_responderToInitiator);
        }
    }
}
