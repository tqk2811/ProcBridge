using System.Security.Cryptography;
using System.Text;
using ProcBridge.Crypto.Pairing.Enums;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Crypto.Pairing;

/// <summary>
/// Pairing on machine A (Broker): the user types the short code shown on machine B.
/// <para>
/// Runs inside a session from a Noise XX handshake that accepted any static key. Messages are
/// carried by <see cref="SecureSession.GetPairingChannel"/>; the session stays
/// untrusted (no data channels) until <see cref="ConfirmCode"/> succeeds. Flow (commit-then-reveal, so a
/// man in the middle cannot grind keys to match codes; success chance per attempt is 1e-6):
/// </para>
/// <list type="number">
/// <item>A: <see cref="CreateCommit"/> -> B</item>
/// <item>B: <see cref="PairingResponder.ReceiveCommit"/> -> nonce -> A</item>
/// <item>A: <see cref="ReceiveNonce"/> -> reveal -> B; B now shows <see cref="PairingResponder.Code"/></item>
/// <item>A: <see cref="ConfirmCode"/> with the typed code; on match -> confirm -> B and A trusts B's key</item>
/// </list>
/// One code attempt only: a wrong code fails the pairing; start a new session to retry.
/// </summary>
public sealed class PairingInitiator
{
    private readonly SecureSession _session;
    private readonly PairingTranscript _transcript;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(PairingTranscript.NonceSize);
    private byte[]? _responderNonce;
    private State _state = State.Start;

    /// <param name="session">Session from the XX pairing handshake (borrowed).</param>
    public PairingInitiator(SecureSession session)
    {
        if (!session.IsPairing)
            throw new ArgumentException("Pairing needs a session from NoiseHandshake.CreatePairing.", nameof(session));
        _session = session;
        _transcript = new PairingTranscript(session);
    }

    /// <summary>True after the typed code matched; then <see cref="PeerPublicKey"/> can be stored as trusted.</summary>
    public bool IsPaired => _state == State.Paired;

    /// <summary>Machine B's identity key; trust it only when <see cref="IsPaired"/>.</summary>
    public byte[] PeerPublicKey => _session.RemoteStaticPublicKey.ToArray();

    public string PeerFingerprint => _session.RemoteFingerprint;

    public byte[] CreateCommit()
    {
        Expect(State.Start);
        _state = State.Committed;
        return PairingTranscript.Pack(PairingMessageType.Commit, _transcript.Commitment(_nonce));
    }

    public byte[] ReceiveNonce(ReadOnlySpan<byte> message)
    {
        Expect(State.Committed);
        _state = State.Failed;
        _responderNonce = PairingTranscript.Unpack(PairingMessageType.Nonce, message);
        _state = State.Revealed;
        return PairingTranscript.Pack(PairingMessageType.Reveal, _nonce);
    }

    /// <summary>Checks the code typed by the user (digits only, separators ignored).</summary>
    /// <returns>The confirm message to send to B.</returns>
    /// <exception cref="CryptoProtocolException">Code does not match: possible attack or typo; pairing is aborted.</exception>
    public byte[] ConfirmCode(string typedCode)
    {
        ArgumentNullException.ThrowIfNull(typedCode);
        Expect(State.Revealed);
        var expected = Encoding.ASCII.GetBytes(_transcript.Code(_nonce, _responderNonce));
        var typed = Encoding.ASCII.GetBytes(PairingTranscript.NormalizeCode(typedCode));
        if (!CryptographicOperations.FixedTimeEquals(expected, typed))
        {
            _state = State.Failed;
            throw new CryptoProtocolException("Pairing code does not match.");
        }
        _state = State.Paired;
        _session.MarkTrusted();
        return PairingTranscript.Pack(PairingMessageType.Confirm, _transcript.Confirmation(_nonce, _responderNonce));
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
        Paired,
        Failed,
    }
}
