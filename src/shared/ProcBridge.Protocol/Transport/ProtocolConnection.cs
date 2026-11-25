using System.Collections.Concurrent;
using System.Net.Sockets;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Protocol.Transport;

/// <summary>
/// Client side of one connection: multiplexes many requests by request id over one <see cref="IFrameChannel"/>.
/// Owns the channel. Once the connection breaks every pending request fails with
/// <see cref="ProtocolStatusException.IsConnectionFailure"/> and the object stays dead (the pool reconnects).
/// </summary>
public sealed class ProtocolConnection : IAsyncDisposable
{
    private readonly IFrameChannel _channel;
    private readonly TransportOptions _options;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Frame>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _probeRequested = new(0, 1);
    private Task _readLoop = Task.CompletedTask;
    private Task _keepAliveLoop = Task.CompletedTask;
    private long _lastReceived = Environment.TickCount64;
    private long _lastSent = Environment.TickCount64;
    private int _nextRequestId;
    private long _completedRequests;
    private volatile bool _alive = true;
    private int _disposed;

    private ProtocolConnection(IFrameChannel channel, TransportOptions options)
    {
        _channel = channel;
        _options = options;
    }

    /// <summary>Server answer to the Hello exchange.</summary>
    public HelloResponse Server { get; private set; } = null!;

    /// <summary>Round trip of the Hello exchange: a small request on a new, idle connection (base latency of the link).</summary>
    public TimeSpan HelloRoundTrip { get; private set; }

    /// <summary>
    /// Raised on the reader thread for every notification frame (server push, see <see cref="FrameKind.Notification"/>).
    /// Handlers must be quick and must not throw; the frame memory stays valid after the call.
    /// </summary>
    public event Action<Frame>? NotificationReceived;

    /// <summary>Completes when the connection is dead (peer closed, I/O error or dispose).</summary>
    public Task Closed => _closed.Task;

    public bool IsAlive => _alive;

    public int PendingCount => _pending.Count;

    public long CompletedRequests => Interlocked.Read(ref _completedRequests);

    /// <summary>Connects, starts the reader and performs the Hello exchange.</summary>
    public static async Task<ProtocolConnection> OpenAsync(IConnectionFactory factory, HelloRequest hello, TransportOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);

        IFrameChannel channel;
        try
        {
            channel = await factory.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProtocolStatusException(NtStatus.IoTimeout, $"Connecting to {factory.Description} timed out.", isConnectionFailure: true, inner: e);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new ProtocolStatusException(NtStatus.NetworkUnreachable, $"Cannot connect to {factory.Description}: {e.Message}", isConnectionFailure: true, inner: e);
        }

        var connection = new ProtocolConnection(channel, options);
        connection._readLoop = Task.Run(connection.ReadLoopAsync);
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Frame frame = await connection.SendAsync(MessageType.Hello, ProtocolSerializer.Serialize(hello), default, timeout.Token).ConfigureAwait(false);
            connection.HelloRoundTrip = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            HelloResponse response = ProtocolSerializer.Deserialize<HelloResponse>(frame.Header);
            if (response.ProtocolVersion != hello.ProtocolVersion)
                throw new ProtocolStatusException(NtStatus.RevisionMismatch, $"Server speaks protocol {response.ProtocolVersion}, client {hello.ProtocolVersion}.");
            connection.Server = response;
            if (options.KeepAliveInterval > TimeSpan.Zero)
                connection._keepAliveLoop = Task.Run(connection.KeepAliveLoopAsync);
            return connection;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ProtocolStatusException(NtStatus.IoTimeout, $"Hello to {factory.Description} timed out.", isConnectionFailure: true, inner: e);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends a request and waits for its response. An error frame from the server is thrown as
    /// <see cref="ProtocolStatusException"/>; no answer within <see cref="TransportOptions.RequestTimeout"/> gives
    /// <see cref="NtStatus.IoTimeout"/>.
    /// </summary>
    public Task<Frame> SendAsync(MessageType type, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => SendCoreAsync(type, header, payload, _options.RequestTimeout, cancellationToken);

    private async Task<Frame> SendCoreAsync(MessageType type, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        if (!_alive)
            throw ConnectionLost("Connection is closed.");

        uint id = (uint)Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        // The reader may have failed between the check above and the insert; it would not see this entry.
        if (!_alive)
        {
            _pending.TryRemove(id, out _);
            throw ConnectionLost("Connection is closed.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            try
            {
                await _channel.SendAsync(new Frame { Kind = FrameKind.Request, MessageType = type, RequestId = id, Header = header, Payload = payload }, timeout.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastSent, Environment.TickCount64);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // A frame may be half written, so the connection is dead whatever the cause.
                Fail("Send failed: " + e.Message);
                await DisposeChannelQuietlyAsync().ConfigureAwait(false);
                if (e.InnerException is OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    throw new ProtocolStatusException(NtStatus.IoTimeout, $"{type} request timed out while sending.", inner: e);
                }
                throw ConnectionLost("Send failed: " + e.Message, e);
            }

            Frame response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _completedRequests);
            if (response.Kind == FrameKind.Error)
            {
                ErrorResponse error = ProtocolSerializer.Deserialize<ErrorResponse>(response.Header);
                throw new ProtocolStatusException(error.Status, error.Message);
            }
            if (response.MessageType != type)
                throw new ProtocolStatusException(NtStatus.ProtocolError, $"Expected {type} response, got {response.MessageType}.");
            return response;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // A slow request or a dead peer: the probe tells them apart and kills the connection in the second case.
            if (type != MessageType.Ping)
                RequestProbe();
            throw new ProtocolStatusException(NtStatus.IoTimeout, $"{type} request timed out.", inner: e);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Asks the keepalive loop to probe now (no-op when keepalive is off or a probe is already asked for).</summary>
    private void RequestProbe()
    {
        if (_options.KeepAliveInterval <= TimeSpan.Zero)
            return;
        try
        {
            if (_probeRequested.CurrentCount == 0)
                _probeRequested.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another caller asked at the same time.
        }
    }

    /// <summary>
    /// Probes the peer with <see cref="MessageType.Ping"/> once nothing came in, or nothing went out, for
    /// <see cref="TransportOptions.KeepAliveInterval"/> (or a request timed out). The second case keeps a connection that
    /// only receives (a change watch fed by notifications) alive at the Agent, which drops a client it hears nothing from. Any frame received while the probe waits proves the peer alive (a large response
    /// may hold the answer back); silence for <see cref="TransportOptions.KeepAliveTimeout"/> kills the connection.
    /// </summary>
    private async Task KeepAliveLoopAsync()
    {
        try
        {
            while (_alive)
            {
                long now = Environment.TickCount64;
                long idle = Math.Max(now - Interlocked.Read(ref _lastReceived), now - Interlocked.Read(ref _lastSent));
                long wait = (long)_options.KeepAliveInterval.TotalMilliseconds - idle;
                if (wait > 0 && !await _probeRequested.WaitAsync(TimeSpan.FromMilliseconds(wait), _closing.Token).ConfigureAwait(false))
                    continue; // Woken by the timer: frames may have arrived meanwhile, compute again.
                _probeRequested.Wait(0); // This probe also serves a request made meanwhile; do not probe twice.

                if (!await ProbeAsync().ConfigureAwait(false))
                {
                    Fail($"No answer from the peer for {_options.KeepAliveTimeout.TotalSeconds:0.#} s (keepalive).");
                    await DisposeChannelQuietlyAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (ObjectDisposedException)
        {
            // Disposed.
        }
    }

    /// <summary>True when the peer answered or sent anything during the probe.</summary>
    private async Task<bool> ProbeAsync()
    {
        long started = Environment.TickCount64;
        try
        {
            await SendCoreAsync(MessageType.Ping, default, default, _options.KeepAliveTimeout, _closing.Token).ConfigureAwait(false);
            return true;
        }
        catch (ProtocolStatusException e) when (e.Status == NtStatus.IoTimeout && !e.IsConnectionFailure)
        {
            return Interlocked.Read(ref _lastReceived) >= started;
        }
        catch (ProtocolStatusException)
        {
            // An error frame (a peer without Ping support) is an answer; a connection failure already killed it.
            return true;
        }
    }

    private async Task ReadLoopAsync()
    {
        string reason = "Connection closed by peer.";
        try
        {
            while (true)
            {
                Frame? frame = await _channel.ReceiveAsync(_closing.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                if (frame.Value.Kind == FrameKind.Request)
                {
                    reason = "Server sent a request frame.";
                    break;
                }
                if (frame.Value.Kind == FrameKind.Notification)
                {
                    try
                    {
                        NotificationReceived?.Invoke(frame.Value);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // A faulty handler must not kill the connection the requests share.
                    }
                    continue;
                }
                // Responses for requests that already timed out are dropped.
                if (_pending.TryGetValue(frame.Value.RequestId, out TaskCompletionSource<Frame>? completion))
                    completion.TrySetResult(frame.Value);
            }
        }
        catch (Exception e)
        {
            reason = "Connection lost: " + e.Message;
        }
        finally
        {
            Fail(reason);
        }
    }

    private async ValueTask DisposeChannelQuietlyAsync()
    {
        try
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // Already broken; closing is best effort.
        }
    }

    private void Fail(string reason)
    {
        _alive = false;
        _closed.TrySetResult();
        foreach (KeyValuePair<uint, TaskCompletionSource<Frame>> pair in _pending)
            pair.Value.TrySetException(ConnectionLost(reason));
    }

    private static ProtocolStatusException ConnectionLost(string reason, Exception? inner = null)
        => new(NtStatus.NetworkUnreachable, reason, isConnectionFailure: true, inner);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _alive = false;
        _closing.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch
        {
            // The reader reports through Fail; nothing more to do here.
        }
        await _keepAliveLoop.ConfigureAwait(false);
        Fail("Connection disposed.");
        _closing.Dispose();
        _probeRequested.Dispose();
    }
}
