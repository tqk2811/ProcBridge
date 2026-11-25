using System.Buffers.Binary;
using System.Text;
using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Crypto.Noise.Enums;
using ProcBridge.Crypto.Pairing;
using ProcBridge.Crypto.Sessions;
using ProcBridge.Transport.Secure.Enums;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// Server side (machine B) of the encrypted transport: turns each accepted stream into a <see cref="SecureFrameChannel"/>
/// (Noise IK handshake or join of a known session, see <see cref="SecureWire"/>), or runs a pairing on it. Only machines
/// in the paired store get a channel; a session lives while it has channels and is dropped when its last one closes or
/// its machine is revoked. The identity key and the store are borrowed. Thread-safe.
/// </summary>
public sealed class SecureServer : IDisposable
{
    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _pairedMachines;
    private readonly SecureServerOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingByAddress = new(StringComparer.Ordinal);
    private int _pending;
    private CancellationTokenSource? _pairingCts;
    private volatile bool _allowPairing;
    private int _disposed;

    public SecureServer(IdentityKey identity, PairedMachineStore pairedMachines, SecureServerOptions options)
    {
        _identity = identity;
        _pairedMachines = pairedMachines;
        _options = options;
        _allowPairing = options.AllowPairing;
        _pairedMachines.Revoked += OnRevoked;
    }

    /// <summary>
    /// A machine A asks to pair. Raised on the connection's task once the code is known; handlers must return quickly and
    /// decide later through <see cref="PairingRequest.Accept"/> or <see cref="PairingRequest.Reject"/>. Without a handler
    /// the request is rejected.
    /// </summary>
    public event Action<PairingRequest>? PairingRequested;

    /// <summary>Raised when a paired machine was revoked; its open sessions are already dropped.</summary>
    public event Action<string>? PeerRevoked;

    /// <summary>
    /// Accept pairing requests; the UI turns it on while the user pairs a new machine. Turning it off also cancels the
    /// pairing that is waiting for the user's decision.
    /// </summary>
    public bool AllowPairing
    {
        get => _allowPairing;
        set
        {
            _allowPairing = value;
            if (value)
                return;
            CancellationTokenSource? pairing;
            lock (_lock)
                pairing = _pairingCts;
            try
            {
                pairing?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public PairedMachineStore PairedMachines => _pairedMachines;

    /// <summary>Fingerprint of this machine's identity key (shown to the user while pairing).</summary>
    public string Fingerprint => _identity.Fingerprint;

    /// <summary>Sessions with at least one open channel.</summary>
    public int SessionCount
    {
        get
        {
            lock (_lock)
                return _sessions.Count;
        }
    }

    /// <summary>
    /// Runs the preamble of a new connection. Returns its channel, or null when the connection carried a pairing, was
    /// refused or failed; in that case the stream is closed. Takes ownership of <paramref name="stream"/>.
    /// </summary>
    /// <param name="remoteAddress">Shown in pairing requests only.</param>
    public async Task<SecureFrameChannel?> AcceptAsync(Stream stream, string remoteAddress, CancellationToken cancellationToken)
    {
        SecureFrameChannel? channel = null;
        string? slotKey = TryTakeHandshakeSlot(remoteAddress);
        if (slotKey is null)
        {
            // Too many connections are still in their preamble or handshake (global or from this address): drop it at once.
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.HandshakeTimeout);
            SecureConnectionOp? op = await SecureWire.ReadPreambleAsync(stream, timeout.Token).ConfigureAwait(false);
            switch (op)
            {
                case SecureConnectionOp.Handshake:
                    channel = await HandshakeAsync(stream, timeout.Token).ConfigureAwait(false);
                    break;
                case SecureConnectionOp.Join:
                    channel = await JoinAsync(stream, timeout.Token).ConfigureAwait(false);
                    break;
                case SecureConnectionOp.Pairing when _allowPairing:
                    // A pairing waits for the user for minutes, so it no longer counts as a connection in its handshake;
                    // one pairing at a time is allowed instead.
                    ReleaseHandshakeSlot(ref slotKey);
                    await PairAsync(stream, remoteAddress, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is CryptoProtocolException or IOException or EndOfStreamException or OperationCanceledException
            or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // Untrusted key, bad message, timeout or a peer that went away: the connection is just closed.
        }
        finally
        {
            ReleaseHandshakeSlot(ref slotKey);
        }
        if (channel is null)
            await stream.DisposeAsync().ConfigureAwait(false);
        return channel;
    }

    /// <summary>Counts a connection in its handshake against the global and the per-address limits; null when over a limit.</summary>
    private string? TryTakeHandshakeSlot(string remoteAddress)
    {
        string key = AddressKey(remoteAddress);
        lock (_lock)
        {
            if (_pending >= _options.MaxPendingHandshakes)
                return null;
            _pendingByAddress.TryGetValue(key, out int perAddress);
            if (perAddress >= _options.MaxPendingHandshakesPerAddress)
                return null;
            _pendingByAddress[key] = perAddress + 1;
            _pending++;
            return key;
        }
    }

    private void ReleaseHandshakeSlot(ref string? key)
    {
        if (key is null)
            return;
        lock (_lock)
        {
            _pending--;
            if (_pendingByAddress[key] <= 1)
                _pendingByAddress.Remove(key);
            else
                _pendingByAddress[key]--;
        }
        key = null;
    }

    /// <summary>The host part of <c>host:port</c> (port dropped so a client cannot dodge the limit with new source ports).</summary>
    private static string AddressKey(string remoteAddress)
        => System.Net.IPEndPoint.TryParse(remoteAddress, out System.Net.IPEndPoint? endPoint) ? endPoint.Address.ToString() : remoteAddress;

    private async Task<SecureFrameChannel?> HandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var handshake = new NoiseHandshake(NoisePattern.IK, isInitiator: false, _identity, SecureWire.Prologue, default, key => _pairedMachines.IsTrusted(key.AsSpan()));
        // Throws for a machine that is not paired, before anything is sent back.
        handshake.ReadMessage(await SecureWire.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false));
        await SecureWire.WriteMessageAsync(stream, handshake.WriteMessage(default), cancellationToken).ConfigureAwait(false);
        SecureSession session = handshake.CreateSession();
        uint channelId = await SecureWire.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);

        var entry = new SessionEntry(session);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _sessions[SecureWire.SessionKey(session.HandshakeHash)] = entry;
        }
        return OpenChannel(stream, entry, channelId);
    }

    private async Task<SecureFrameChannel?> JoinAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] request = new byte[SecureWire.SessionIdSize + sizeof(uint)];
        await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
        string key = Convert.ToHexString(request.AsSpan(0, SecureWire.SessionIdSize));
        uint channelId = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(SecureWire.SessionIdSize));

        SessionEntry? entry;
        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out entry) && !_pairedMachines.IsTrusted(entry.Session.RemoteFingerprint))
                entry = null;
            // Counted now so the session cannot be dropped between the answer and the channel.
            entry?.AddChannel();
        }
        if (entry is null)
        {
            await stream.WriteAsync(new[] { (byte)JoinResult.UnknownSession }, cancellationToken).ConfigureAwait(false);
            return null;
        }
        try
        {
            await stream.WriteAsync(new[] { (byte)JoinResult.Accepted }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return OpenChannel(stream, entry, channelId, counted: true);
        }
        catch
        {
            ReleaseChannel(key, entry);
            throw;
        }
    }

    /// <summary>Accepts the peer's channel id; a reused or wrong-parity id throws (replayed connection).</summary>
    private SecureFrameChannel OpenChannel(Stream stream, SessionEntry entry, uint channelId, bool counted = false)
    {
        string key = SecureWire.SessionKey(entry.Session.HandshakeHash);
        if (!counted)
        {
            lock (_lock)
                entry.AddChannel();
        }
        try
        {
            SecureChannel channel = entry.Session.AcceptChannel(channelId);
            return new SecureFrameChannel(stream, channel, _options.MaxFrameSize, entry.Session.RemoteFingerprint, () => ReleaseChannel(key, entry));
        }
        catch
        {
            ReleaseChannel(key, entry);
            throw;
        }
    }

    private void ReleaseChannel(string key, SessionEntry entry)
    {
        lock (_lock)
        {
            if (entry.RemoveChannel() > 0)
                return;
            if (_sessions.TryGetValue(key, out SessionEntry? current) && current == entry)
                _sessions.Remove(key);
        }
        entry.Session.Dispose();
    }

    private void OnRevoked(PairedMachine machine)
    {
        lock (_lock)
        {
            foreach (KeyValuePair<string, SessionEntry> pair in _sessions.Where(x => x.Value.Session.RemoteFingerprint == machine.Fingerprint).ToArray())
                _sessions.Remove(pair.Key);
        }
        PeerRevoked?.Invoke(machine.Fingerprint);
    }

    /// <summary>
    /// Pairing on machine B: Noise XX, A's name, commit / nonce / reveal, then <see cref="PairingRequested"/> with the code;
    /// A's confirm proves it typed the code, and only the user's <see cref="PairingRequest.Accept"/> stores A as paired.
    /// The last message tells A the decision and B's name.
    /// </summary>
    private async Task PairAsync(Stream stream, string remoteAddress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.PairingTimeout);
        CancellationToken token = timeout.Token;
        lock (_lock)
        {
            // Only one pairing waits for the user at a time: a second one would just stack prompts and codes.
            if (_pairingCts is not null)
                return;
            _pairingCts = timeout;
        }
        try
        {
            if (!_allowPairing)
                return;
            await RunPairingAsync(stream, remoteAddress, token).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                _pairingCts = null;
        }
    }

    private async Task RunPairingAsync(Stream stream, string remoteAddress, CancellationToken token)
    {
        using var handshake = NoiseHandshake.CreatePairing(isInitiator: false, _identity, SecureWire.Prologue);
        handshake.ReadMessage(await SecureWire.ReadMessageAsync(stream, token).ConfigureAwait(false));
        await SecureWire.WriteMessageAsync(stream, handshake.WriteMessage(default), token).ConfigureAwait(false);
        handshake.ReadMessage(await SecureWire.ReadMessageAsync(stream, token).ConfigureAwait(false));
        using SecureSession session = handshake.CreateSession();
        using SecureChannel channel = session.GetPairingChannel();
        var responder = new PairingResponder(session);

        string peerName = DecodeName(await SecureWire.ReadSealedAsync(stream, channel, token).ConfigureAwait(false));
        byte[] nonce = responder.ReceiveCommit(await SecureWire.ReadSealedAsync(stream, channel, token).ConfigureAwait(false));
        await SecureWire.WriteSealedAsync(stream, channel, nonce, token).ConfigureAwait(false);
        responder.ReceiveReveal(await SecureWire.ReadSealedAsync(stream, channel, token).ConfigureAwait(false));

        var request = new PairingRequest(responder.Code!, responder.PeerFingerprint, remoteAddress, peerName);
        bool paired = false;
        try
        {
            Action<PairingRequest>? handler = PairingRequested;
            if (handler is null)
                request.Reject();
            else
                handler(request);

            Task<byte[]> confirm = SecureWire.ReadSealedAsync(stream, channel, token).AsTask();
            Task first = await Task.WhenAny(confirm, request.Decision).ConfigureAwait(false);
            if (first == request.Decision && !await request.Decision.ConfigureAwait(false))
            {
                await SendDecisionAsync(stream, channel, accepted: false, token).ConfigureAwait(false);
                return;
            }

            responder.ReceiveConfirm(await confirm.ConfigureAwait(false));
            bool accepted = await request.Decision.WaitAsync(token).ConfigureAwait(false);
            // The user may have turned pairing off while deciding: nothing is stored then.
            if (accepted && !_allowPairing)
                accepted = false;
            if (accepted)
            {
                responder.Accept();
                _pairedMachines.Add(responder.PeerPublicKey, peerName);
            }
            await SendDecisionAsync(stream, channel, accepted, token).ConfigureAwait(false);
            paired = accepted;
        }
        finally
        {
            request.Complete(paired);
        }
    }

    private async Task SendDecisionAsync(Stream stream, SecureChannel channel, bool accepted, CancellationToken cancellationToken)
    {
        byte[] name = EncodeName(Environment.MachineName);
        byte[] message = new byte[1 + name.Length];
        message[0] = accepted ? (byte)1 : (byte)0;
        name.CopyTo(message, 1);
        await SecureWire.WriteSealedAsync(stream, channel, message, cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] EncodeName(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        return bytes.Length <= SecureWire.MaxNameBytes ? bytes : bytes[..SecureWire.MaxNameBytes];
    }

    internal static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SecureWire.MaxNameBytes)
            throw new CryptoProtocolException("Machine name too long.");
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _pairedMachines.Revoked -= OnRevoked;
        lock (_lock)
            _sessions.Clear();
    }

    /// <summary>A session and its number of open channels; the count is changed under the server lock.</summary>
    private sealed class SessionEntry(SecureSession session)
    {
        private int _channels;

        public SecureSession Session { get; } = session;

        public void AddChannel() => _channels++;

        public int RemoveChannel() => --_channels;
    }
}
