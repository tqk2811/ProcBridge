using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Crypto.Noise.Enums;
using ProcBridge.Crypto.Sessions;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Transport.Secure.Enums;
using ProcBridge.Transport.Secure.Interfaces;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// Client side (machine A) of the encrypted transport. The first connection runs a Noise IK handshake against the
/// Agent's paired key and keeps the resulting <see cref="SecureSession"/>; every later connection of the pool joins that
/// session as a new <see cref="SecureChannel"/> (id sent in clear), so one handshake serves all parallel connections.
/// When the Agent no longer knows the session (restart) a join is refused and the next connection handshakes again.
/// <para>
/// The peer must be in <paramref name="pairedMachines"/> at every handshake and join, so revoking it stops new
/// connections at once. The identity key and the store are borrowed; the connector is disposed with the factory when
/// it is disposable (the relay connector holds an HTTP client).
/// </para>
/// </summary>
public sealed class SecureConnectionFactory : IConnectionFactory, IDisposable
{
    private readonly IStreamConnector _connector;
    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _pairedMachines;
    private readonly byte[] _agentPublicKey;
    private readonly string _agentFingerprint;
    private readonly int _maxFrameSize;
    private readonly TimeSpan _handshakeTimeout;
    private readonly SemaphoreSlim _handshakeLock = new(1, 1);
    private SecureSession? _session;

    /// <param name="agentPublicKey">Identity key of the Agent, from pairing.</param>
    public SecureConnectionFactory(IStreamConnector connector, IdentityKey identity, PairedMachineStore pairedMachines, ReadOnlySpan<byte> agentPublicKey, TransportOptions options)
    {
        _connector = connector;
        _identity = identity;
        _pairedMachines = pairedMachines;
        _agentPublicKey = agentPublicKey.ToArray();
        _agentFingerprint = KeyFingerprint.Compute(agentPublicKey);
        _maxFrameSize = options.MaxFrameSize;
        _handshakeTimeout = options.HandshakeTimeout;
    }

    public string Description => $"secure tcp://{_connector.Description}";

    /// <summary>Number of Noise handshakes run so far (tests: joins must not handshake again).</summary>
    public int HandshakeCount { get; private set; }

    public async ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!_pairedMachines.IsTrusted(_agentPublicKey))
            throw new ProtocolStatusException(NtStatus.AccessDenied, $"Agent {_agentFingerprint} is not paired.");

        SecureSession? session = Volatile.Read(ref _session);
        if (session is not null)
        {
            IFrameChannel? joined = await WithTimeoutAsync(token => TryJoinAsync(session, token), cancellationToken).ConfigureAwait(false);
            if (joined is not null)
                return joined;
        }

        await _handshakeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another connection may have handshaken while this one waited.
            SecureSession? current = Volatile.Read(ref _session);
            if (current is not null && current != session)
            {
                IFrameChannel? joined = await WithTimeoutAsync(token => TryJoinAsync(current, token), cancellationToken).ConfigureAwait(false);
                if (joined is not null)
                    return joined;
            }
            return await WithTimeoutAsync(HandshakeAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _handshakeLock.Release();
        }
    }

    /// <summary>
    /// Runs one connect step (handshake or join) under <see cref="TransportOptions.HandshakeTimeout"/>: an Agent that
    /// accepts the connection and then says nothing fails the step instead of holding the caller for ever.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> step, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_handshakeTimeout);
        try
        {
            return await step(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProtocolStatusException(NtStatus.IoTimeout, $"Secure handshake with {_connector.Description} timed out.", isConnectionFailure: true, inner: e);
        }
    }

    private async Task<IFrameChannel> HandshakeAsync(CancellationToken cancellationToken)
    {
        Stream stream = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var handshake = new NoiseHandshake(NoisePattern.IK, isInitiator: true, _identity, SecureWire.Prologue, _agentPublicKey, key => _pairedMachines.IsTrusted(key.AsSpan()));
            await SecureWire.WritePreambleAsync(stream, SecureConnectionOp.Handshake, cancellationToken).ConfigureAwait(false);
            await SecureWire.WriteMessageAsync(stream, handshake.WriteMessage(default), cancellationToken).ConfigureAwait(false);
            byte[] reply;
            try
            {
                reply = await SecureWire.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException e)
            {
                // The Agent closes without answering when it does not know this machine.
                throw new ProtocolStatusException(NtStatus.AccessDenied, $"Agent {_connector.Description} refused the handshake (not paired?).", inner: e);
            }
            handshake.ReadMessage(reply);
            SecureSession session = handshake.CreateSession();
            HandshakeCount++;
            SecureChannel channel = session.OpenChannel();
            await SecureWire.WriteUInt32Async(stream, channel.Id, cancellationToken).ConfigureAwait(false);
            SecureSession? previous = Interlocked.Exchange(ref _session, session);
            previous?.Dispose();
            return new SecureFrameChannel(stream, channel, _maxFrameSize, session.RemoteFingerprint);
        }
        catch (CryptoProtocolException e)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new ProtocolStatusException(NtStatus.AccessDenied, $"Secure handshake with {_connector.Description} failed: {e.Message}", inner: e);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a channel of <paramref name="session"/> on a new stream; null when the Agent refused the join.</summary>
    private async Task<IFrameChannel?> TryJoinAsync(SecureSession session, CancellationToken cancellationToken)
    {
        SecureChannel channel;
        try
        {
            channel = session.OpenChannel();
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }

        Stream? stream = null;
        try
        {
            stream = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
            byte[] request = new byte[SecureWire.Magic.Length + 1 + SecureWire.SessionIdSize + sizeof(uint)];
            SecureWire.Magic.CopyTo(request);
            request[SecureWire.Magic.Length] = (byte)SecureConnectionOp.Join;
            session.HandshakeHash[..SecureWire.SessionIdSize].CopyTo(request.AsSpan(SecureWire.Magic.Length + 1));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(request.Length - sizeof(uint)), channel.Id);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            byte[] result = new byte[1];
            int read = await stream.ReadAsync(result, cancellationToken).ConfigureAwait(false);
            if (read == 1 && result[0] == (byte)JoinResult.Accepted)
                return new SecureFrameChannel(stream, channel, _maxFrameSize, session.RemoteFingerprint);

            // Unknown session on the Agent: forget it so the caller handshakes again.
            Interlocked.CompareExchange(ref _session, null, session);
            channel.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            channel.Dispose();
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        (_connector as IDisposable)?.Dispose();
        Interlocked.Exchange(ref _session, null)?.Dispose();
        _handshakeLock.Dispose();
    }
}
