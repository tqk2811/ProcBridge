using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Broker;

/// <summary>
/// Keeps one dedicated connection to the Agent subscribed with <see cref="MessageType.FsWatch"/> and raises the
/// change notifications it receives (plan section 10, M7). The connection is separate from the request pool so
/// notifications never queue behind file data and pool reconnects do not drop the subscription. When the connection
/// breaks it reconnects after <c>reconnectDelay</c>; while it is down <see cref="IsActive"/> is false and the cache
/// falls back to its TTL. Owns its connection.
/// </summary>
public sealed class ChangeWatcher : IAsyncDisposable
{
    private readonly IConnectionFactory _factory;
    private readonly HelloRequest _hello;
    private readonly TransportOptions _options;
    private readonly TimeSpan _reconnectDelay;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _stateLock = new();
    private TaskCompletionSource _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    public ChangeWatcher(IConnectionFactory factory, HelloRequest hello, TransportOptions options, TimeSpan reconnectDelay)
    {
        _factory = factory;
        _hello = hello;
        _options = options;
        _reconnectDelay = reconnectDelay;
    }

    /// <summary>True while subscribed: every change on machine B since <see cref="StateChanged"/> reported true arrives.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Number of successful subscriptions (first one and reconnects).</summary>
    public int SubscribeCount { get; private set; }

    /// <summary>
    /// Raised with true right after a subscription succeeded and with false when it is lost. Changes made while
    /// inactive are never reported, so a handler must treat everything cached before "true" as stale.
    /// </summary>
    public event Action<bool>? StateChanged;

    /// <summary>Raised for every notification, on the connection's reader thread, one at a time and in order.</summary>
    public event Action<FsChangeNotification>? Changed;

    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>Completes when the watcher is subscribed (now or later).</summary>
    public Task WaitActiveAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
            return _active.Task.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            ProtocolConnection? connection = null;
            try
            {
                connection = await ProtocolConnection.OpenAsync(_factory, _hello, _options, _stopping.Token).ConfigureAwait(false);
                connection.NotificationReceived += OnNotification;
                await connection.SendAsync(MessageType.FsWatch, ProtocolSerializer.Serialize(new FsWatchRequest()), default, _stopping.Token).ConfigureAwait(false);
                SetActive(true);
                await connection.Closed.WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Agent unreachable, refused or the connection broke; retry below.
            }
            finally
            {
                SetActive(false);
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(_reconnectDelay, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnNotification(Frame frame)
    {
        if (frame.MessageType != MessageType.FsChanged)
            return;
        FsChangeNotification notification;
        try
        {
            notification = ProtocolSerializer.Deserialize<FsChangeNotification>(frame.Header);
        }
        catch (ProtocolStatusException)
        {
            // Unreadable notification: something was lost, which is what a rescan says.
            notification = new FsChangeNotification { Changes = [], Rescan = true };
        }
        Changed?.Invoke(notification);
    }

    private void SetActive(bool active)
    {
        lock (_stateLock)
        {
            if (IsActive == active)
                return;
            IsActive = active;
            if (active)
            {
                SubscribeCount++;
            }
            else
            {
                _active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        StateChanged?.Invoke(active);
        // Completed after the handlers ran, so a waiter sees the cache already in watch mode.
        if (active)
        {
            lock (_stateLock)
            {
                if (IsActive)
                    _active.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        await _loop.ConfigureAwait(false);
        _stopping.Dispose();
    }
}
