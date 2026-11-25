using System.Security.Cryptography;
using ProcBridge.Crypto.Pairing.Enums;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Crypto.Pairing;

/// <summary>
/// Pairing on machine B (Agent): shows <see cref="Code"/> on the WPF window for the user to type on A.
/// See <see cref="PairingInitiator"/> for the flow.
/// <para>
/// The human channel only goes from B to A, so the confirm message proves the code matched on A
/// but cannot by itself prove the sender is the real A (a man in the middle also knows its own
/// code). Therefore B must not trust the key automatically: after <see cref="ReceiveConfirm"/>
/// the UI asks the user on B to accept (A reported success, fingerprint <see cref="PeerFingerprint"/>
/// matches what A shows), then calls <see cref="Accept"/>.
/// </para>
/// </summary>
public sealed class PairingResponder
{
    private readonly SecureSession _session;
    private readonly PairingTranscript _transcript;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(PairingTranscript.NonceSize);
    private byte[]? _commitment;
    private byte[]? _initiatorNonce;
    private State _state = State.Start;

    /// <param name="session">Session from the XX pairing handshake (borrowed).</param>
    public PairingResponder(SecureSession session)
    {
        _session = session;
        _transcript = new PairingTranscript(session);
    }

    /// <summary>6-digit code to display, available after <see cref="ReceiveReveal"/>.</summary>
    public string? Code { get; private set; }

    /// <summary>True when A confirmed the code; the UI should now ask the user to accept.</summary>
    public bool IsConfirmedByPeer => _state is State.Confirmed or State.Paired;

    /// <summary>True after <see cref="Accept"/>; then <see cref="PeerPublicKey"/> can be stored as trusted.</summary>
    public bool IsPaired => _state == State.Paired;

    public byte[] PeerPublicKey => _session.RemoteStaticPublicKey.ToArray();

    public string PeerFingerprint => _session.RemoteFingerprint;

    public byte[] ReceiveCommit(ReadOnlySpan<byte> message)
    {
        Expect(State.Start);
        _state = State.Failed;
        _commitment = PairingTranscript.Unpack(PairingMessageType.Commit, message);
        _state = State.Committed;
        return PairingTranscript.Pack(PairingMessageType.Nonce, _nonce);
    }

    /// <exception cref="CryptoProtocolException">Revealed nonce does not match the commitment.</exception>
    public void ReceiveReveal(ReadOnlySpan<byte> message)
    {
        Expect(State.Committed);
        _state = State.Failed;
        var nonce = PairingTranscript.Unpack(PairingMessageType.Reveal, message);
        if (!CryptographicOperations.FixedTimeEquals(_transcript.Commitment(nonce), _commitment))
            throw new CryptoProtocolException("Pairing reveal does not match the commitment.");
        _initiatorNonce = nonce;
        Code = _transcript.Code(_initiatorNonce, _nonce);
        _state = State.Revealed;
    }

    /// <exception cref="CryptoProtocolException">Confirmation is invalid.</exception>
    public void ReceiveConfirm(ReadOnlySpan<byte> message)
    {
        Expect(State.Revealed);
        _state = State.Failed;
        var body = PairingTranscript.Unpack(PairingMessageType.Confirm, message);
        if (!CryptographicOperations.FixedTimeEquals(_transcript.Confirmation(_initiatorNonce, _nonce), body))
            throw new CryptoProtocolException("Pairing confirmation is invalid.");
        _state = State.Confirmed;
    }

    /// <summary>The user on B approved the pairing after A reported success.</summary>
    public void Accept()
    {
        Expect(State.Confirmed);
        _state = State.Paired;
    }

    private void Expect(State state)
    {
        if (_state != state)
            throw new InvalidOperationException($"Pairing is in state {_state}, expected {state}.");
    }

    private enum State
    {
        Start,
        Committed,
        Revealed,
        Confirmed,
        Paired,
        Failed,
    }
}
