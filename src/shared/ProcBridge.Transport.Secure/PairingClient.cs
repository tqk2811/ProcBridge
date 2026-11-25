using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Crypto.Pairing;
using ProcBridge.Crypto.Sessions;
using ProcBridge.Transport.Secure.Enums;
using ProcBridge.Transport.Secure.Interfaces;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// First-time pairing on machine A (see <see cref="SecureServer"/> for machine B): Noise XX with the Agent, then the
/// short code exchange of <see cref="PairingInitiator"/>. The user types the 6-digit code machine B shows; when it
/// matches and the user on B accepts, the Agent's key is stored in the paired machine store with the connector's
/// address. One attempt per call: a wrong code aborts. The identity key and the store are borrowed.
/// </summary>
public sealed class PairingClient
{
    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _pairedMachines;
    private readonly string _machineName;

    public PairingClient(IdentityKey identity, PairedMachineStore pairedMachines, string? machineName = null)
    {
        _identity = identity;
        _pairedMachines = pairedMachines;
        _machineName = machineName ?? Environment.MachineName;
    }

    /// <summary>Fingerprint of this machine, for the user to compare with what machine B shows.</summary>
    public string Fingerprint => _identity.Fingerprint;

    /// <param name="askCode">
    /// Called once the Agent shows its code; gets the Agent's fingerprint (to show the user) and returns the code the
    /// user typed.
    /// </param>
    /// <returns>The stored Agent.</returns>
    /// <exception cref="PairingFailedException">Wrong code, rejected on machine B, or a protocol failure.</exception>
    public async Task<PairedMachine> PairAsync(IStreamConnector connector, Func<string, CancellationToken, Task<string>> askCode, CancellationToken cancellationToken)
    {
        await using Stream stream = await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var handshake = NoiseHandshake.CreatePairing(isInitiator: true, _identity, SecureWire.Prologue);
            await SecureWire.WritePreambleAsync(stream, SecureConnectionOp.Pairing, cancellationToken).ConfigureAwait(false);
            await SecureWire.WriteMessageAsync(stream, handshake.WriteMessage(default), cancellationToken).ConfigureAwait(false);
            handshake.ReadMessage(await SecureWire.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false));
            await SecureWire.WriteMessageAsync(stream, handshake.WriteMessage(default), cancellationToken).ConfigureAwait(false);
            using SecureSession session = handshake.CreateSession();
            // Through the relay the lookup named a HostId: the key the handshake proves must be that machine's.
            if (connector is IPeerHintConnector { ExpectedPeerFingerprint: { } expected }
                && handshake.RemoteStaticPublicKey is { } peerKey
                && !string.Equals(KeyFingerprint.Compute(peerKey), expected, StringComparison.Ordinal))
                throw new PairingFailedException($"The relay named machine {expected}, but another machine answered: pairing refused.");
            using SecureChannel channel = session.GetPairingChannel();
            var initiator = new PairingInitiator(session);

            await SecureWire.WriteSealedAsync(stream, channel, SecureServer.EncodeName(_machineName), cancellationToken).ConfigureAwait(false);
            await SecureWire.WriteSealedAsync(stream, channel, initiator.CreateCommit(), cancellationToken).ConfigureAwait(false);
            byte[] reveal = initiator.ReceiveNonce(await SecureWire.ReadSealedAsync(stream, channel, cancellationToken).ConfigureAwait(false));
            await SecureWire.WriteSealedAsync(stream, channel, reveal, cancellationToken).ConfigureAwait(false);

            string typed = await askCode(initiator.PeerFingerprint, cancellationToken).ConfigureAwait(false);
            byte[] confirm = initiator.ConfirmCode(typed);
            await SecureWire.WriteSealedAsync(stream, channel, confirm, cancellationToken).ConfigureAwait(false);

            byte[] decision = await SecureWire.ReadSealedAsync(stream, channel, cancellationToken).ConfigureAwait(false);
            if (decision.Length < 1 || decision[0] != 1)
                throw new PairingFailedException("Pairing was rejected on the Agent.");
            string agentName = SecureServer.DecodeName(decision.AsSpan(1));
            return _pairedMachines.Add(initiator.PeerPublicKey, agentName, connector.Description);
        }
        catch (CryptoProtocolException e)
        {
            throw new PairingFailedException(e.Message, e);
        }
        catch (IOException e)
        {
            // EndOfStreamException included: the Agent closes without a word when pairing is off, rejected or timed out.
            throw new PairingFailedException("The Agent closed the connection (pairing not allowed, rejected or timed out).", e);
        }
    }
}
