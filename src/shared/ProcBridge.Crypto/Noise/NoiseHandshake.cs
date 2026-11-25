using System.Security.Cryptography;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise.Enums;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Crypto.Noise;

/// <summary>
/// Noise HandshakeState (Noise spec revision 34, section 5.3) for
/// <c>Noise_XX_25519_ChaChaPoly_SHA256</c> and <c>Noise_IK_25519_ChaChaPoly_SHA256</c>.
/// Transport-agnostic: the caller moves the returned byte arrays over any channel.
/// Any exception leaves the handshake failed; drop the connection and start over.
/// The local static key is borrowed (not disposed here); the ephemeral key is owned.
/// </summary>
public sealed class NoiseHandshake : IDisposable
{
    /// <summary>Noise limit for a single handshake message.</summary>
    public const int MaxMessageSize = 65535;

    private const int KeySize = X25519KeyPair.KeySize;

    private static readonly NoiseToken[][] _xx =
    [
        [NoiseToken.E],
        [NoiseToken.E, NoiseToken.EE, NoiseToken.S, NoiseToken.ES],
        [NoiseToken.S, NoiseToken.SE],
    ];

    private static readonly NoiseToken[][] _ik =
    [
        [NoiseToken.E, NoiseToken.ES, NoiseToken.S, NoiseToken.SS],
        [NoiseToken.E, NoiseToken.EE, NoiseToken.SE],
    ];

    private readonly SymmetricState _symmetric;
    private readonly NoiseToken[][] _messages;
    private readonly X25519KeyPair _localStatic;
    private readonly Func<byte[], bool>? _remoteStaticVerifier;
    private readonly bool _isPairing;
    private X25519KeyPair? _localEphemeral;
    private byte[]? _remoteStatic;
    private byte[]? _remoteEphemeral;
    private int _messageIndex;
    private bool _failed;
    private bool _sessionCreated;
    private byte[]? _handshakeHash;

    /// <param name="pattern">XX for pairing, IK for sessions with a known responder key.</param>
    /// <param name="isInitiator">True on the side that sends the first message (Broker).</param>
    /// <param name="localStatic">This machine's identity key (borrowed).</param>
    /// <param name="prologue">Data both sides must agree on (protocol version); mismatch fails the handshake.</param>
    /// <param name="remoteStaticPublicKey">Responder key, required for the IK initiator, otherwise empty.</param>
    /// <param name="remoteStaticVerifier">
    /// Required. Called once with the peer's static key after it is authenticated; return false to reject
    /// (unknown or unpaired machine). For the IK initiator it is checked once against
    /// <paramref name="remoteStaticPublicKey"/> up front. To accept an unknown key use
    /// <see cref="CreatePairing"/> instead.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="remoteStaticVerifier"/> is null.</exception>
    public NoiseHandshake(
        NoisePattern pattern,
        bool isInitiator,
        IdentityKey localStatic,
        ReadOnlySpan<byte> prologue,
        ReadOnlySpan<byte> remoteStaticPublicKey,
        Func<byte[], bool> remoteStaticVerifier)
        : this(pattern, isInitiator, localStatic.KeyPair, null, prologue, remoteStaticPublicKey,
            remoteStaticVerifier ?? throw new ArgumentNullException(nameof(remoteStaticVerifier)), isPairing: false)
    {
        if (pattern == NoisePattern.IK && isInitiator && !remoteStaticVerifier(remoteStaticPublicKey.ToArray()))
            throw new CryptoProtocolException("Responder static key is not trusted.");
    }

    /// <summary>
    /// Noise XX handshake for first-time pairing: accepts any peer static key. The resulting
    /// <see cref="SecureSession"/> is untrusted until pairing (SAS confirmation) completes, so it
    /// cannot open data channels; see <see cref="SecureSession.IsTrusted"/>.
    /// </summary>
    public static NoiseHandshake CreatePairing(bool isInitiator, IdentityKey localStatic, ReadOnlySpan<byte> prologue)
        => new(NoisePattern.XX, isInitiator, localStatic.KeyPair, null, prologue, default, null, isPairing: true);

    /// <summary>Test hook: fixed ephemeral key for official test vectors (ownership transferred).</summary>
    internal NoiseHandshake(
        NoisePattern pattern,
        bool isInitiator,
        X25519KeyPair localStatic,
        X25519KeyPair? fixedEphemeral,
        ReadOnlySpan<byte> prologue,
        ReadOnlySpan<byte> remoteStaticPublicKey,
        Func<byte[], bool>? remoteStaticVerifier,
        bool isPairing = false)
    {
        Pattern = pattern;
        IsInitiator = isInitiator;
        _localStatic = localStatic;
        _localEphemeral = fixedEphemeral;
        _remoteStaticVerifier = remoteStaticVerifier;
        _isPairing = isPairing;
        _messages = pattern switch
        {
            NoisePattern.XX => _xx,
            NoisePattern.IK => _ik,
            _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unsupported Noise pattern."),
        };

        _symmetric = new SymmetricState($"Noise_{pattern}_25519_ChaChaPoly_SHA256");
        _symmetric.MixHash(prologue);

        if (pattern == NoisePattern.IK)
        {
            // Pre-message "<- s": both sides hash the responder static key.
            if (isInitiator)
            {
                if (remoteStaticPublicKey.Length != KeySize)
                    throw new ArgumentException("IK initiator needs the responder static public key.", nameof(remoteStaticPublicKey));
                _remoteStatic = remoteStaticPublicKey.ToArray();
                _symmetric.MixHash(_remoteStatic);
            }
            else
            {
                _symmetric.MixHash(_localStatic.PublicKey);
            }
        }
        else if (!remoteStaticPublicKey.IsEmpty)
        {
            throw new ArgumentException("XX does not take a pre-known remote static key.", nameof(remoteStaticPublicKey));
        }
    }

    public NoisePattern Pattern { get; }

    public bool IsInitiator { get; }

    /// <summary>True for a handshake made by <see cref="CreatePairing"/> (peer key not verified).</summary>
    public bool IsPairing => _isPairing;

    public bool IsCompleted => _messageIndex >= _messages.Length;

    /// <summary>True when this side must call <see cref="WriteMessage"/> next.</summary>
    public bool IsMyTurn => !IsCompleted && (_messageIndex % 2 == 0) == IsInitiator;

    /// <summary>Peer static public key, available once received (or pre-known for IK initiator).</summary>
    public byte[]? RemoteStaticPublicKey => _remoteStatic is null ? null : (byte[])_remoteStatic.Clone();

    /// <summary>Handshake hash h, usable as channel binding. Valid after completion.</summary>
    public byte[] HandshakeHash => _handshakeHash is null
        ? throw new InvalidOperationException("Handshake is not completed.")
        : (byte[])_handshakeHash.Clone();

    public byte[] WriteMessage(ReadOnlySpan<byte> payload)
    {
        EnsureUsable(writing: true);
        try
        {
            using var output = new MemoryStream();
            foreach (var token in _messages[_messageIndex])
            {
                switch (token)
                {
                    case NoiseToken.E:
                        _localEphemeral ??= X25519KeyPair.Generate();
                        output.Write(_localEphemeral.PublicKey);
                        _symmetric.MixHash(_localEphemeral.PublicKey);
                        break;
                    case NoiseToken.S:
                        output.Write(_symmetric.EncryptAndHash(_localStatic.PublicKey));
                        break;
                    default:
                        MixDh(token);
                        break;
                }
            }
            output.Write(_symmetric.EncryptAndHash(payload));
            if (output.Length > MaxMessageSize)
                throw new ArgumentException("Handshake message exceeds the Noise size limit.", nameof(payload));
            Advance();
            return output.ToArray();
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>Processes a peer message and returns its decrypted payload.</summary>
    /// <exception cref="CryptoProtocolException">Malformed, forged, or untrusted message.</exception>
    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        EnsureUsable(writing: false);
        try
        {
            if (message.Length > MaxMessageSize)
                throw new CryptoProtocolException("Handshake message exceeds the Noise size limit.");
            var offset = 0;
            var receivedStatic = false;
            foreach (var token in _messages[_messageIndex])
            {
                switch (token)
                {
                    case NoiseToken.E:
                        _remoteEphemeral = Take(message, ref offset, KeySize).ToArray();
                        _symmetric.MixHash(_remoteEphemeral);
                        break;
                    case NoiseToken.S:
                        var length = KeySize + (_symmetric.HasKey ? CipherState.TagSize : 0);
                        _remoteStatic = _symmetric.DecryptAndHash(Take(message, ref offset, length));
                        receivedStatic = true;
                        break;
                    default:
                        MixDh(token);
                        break;
                }
            }
            var payload = _symmetric.DecryptAndHash(message[offset..]);
            if (receivedStatic && _remoteStaticVerifier is not null && !_remoteStaticVerifier((byte[])_remoteStatic!.Clone()))
                throw new CryptoProtocolException("Peer static key is not trusted.");
            Advance();
            return payload;
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>Builds the transport session. Call once after <see cref="IsCompleted"/>.</summary>
    public SecureSession CreateSession()
    {
        var (i2r, r2i) = TakeSplit();
        try
        {
            return new SecureSession(IsInitiator, i2r, r2i, _handshakeHash!, _remoteStatic!, isPairing: _isPairing);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(i2r);
            CryptographicOperations.ZeroMemory(r2i);
        }
    }

    /// <summary>Test hook: raw Noise transport CipherStates (initiator-to-responder, responder-to-initiator).</summary>
    internal (CipherState InitiatorToResponder, CipherState ResponderToInitiator) CreateCipherStates()
    {
        var (i2r, r2i) = TakeSplit();
        var c1 = new CipherState();
        var c2 = new CipherState();
        c1.InitializeKey(i2r);
        c2.InitializeKey(r2i);
        CryptographicOperations.ZeroMemory(i2r);
        CryptographicOperations.ZeroMemory(r2i);
        return (c1, c2);
    }

    private (byte[], byte[]) TakeSplit()
    {
        if (!IsCompleted || _failed)
            throw new InvalidOperationException("Handshake is not completed.");
        if (_sessionCreated)
            throw new InvalidOperationException("Session keys were already taken.");
        _sessionCreated = true;
        return _symmetric.Split();
    }

    private void MixDh(NoiseToken token)
    {
        var (local, remote) = token switch
        {
            NoiseToken.EE => (_localEphemeral, _remoteEphemeral),
            NoiseToken.SS => (_localStatic, _remoteStatic),
            NoiseToken.ES => IsInitiator ? (_localEphemeral, _remoteStatic) : (_localStatic, _remoteEphemeral),
            NoiseToken.SE => IsInitiator ? (_localStatic, _remoteEphemeral) : (_localEphemeral, _remoteStatic),
            _ => throw new InvalidOperationException($"Token {token} is not a DH token."),
        };
        if (local is null || remote is null)
            throw new InvalidOperationException($"Missing key for {token}.");
        var shared = local.Agree(remote);
        _symmetric.MixKey(shared);
        CryptographicOperations.ZeroMemory(shared);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> message, ref int offset, int length)
    {
        if (message.Length - offset < length)
            throw new CryptoProtocolException("Handshake message is truncated.");
        var slice = message.Slice(offset, length);
        offset += length;
        return slice;
    }

    private void EnsureUsable(bool writing)
    {
        if (_failed)
            throw new InvalidOperationException("Handshake has failed; start a new one.");
        if (IsCompleted)
            throw new InvalidOperationException("Handshake is already completed.");
        if (IsMyTurn != writing)
            throw new InvalidOperationException(writing ? "Not this side's turn to write." : "Not this side's turn to read.");
    }

    private void Advance()
    {
        _messageIndex++;
        if (IsCompleted)
            _handshakeHash = _symmetric.Hash;
    }

    public void Dispose()
    {
        _symmetric.Dispose();
        _localEphemeral?.Dispose();
        _localEphemeral = null;
    }
}
