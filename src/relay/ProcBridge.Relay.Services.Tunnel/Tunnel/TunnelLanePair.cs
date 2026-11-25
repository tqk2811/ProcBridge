using System.Net.WebSockets;

using ProcBridge.Relay.Services.Tunnel.Enums;

namespace ProcBridge.Relay.Services.Tunnel;

/// <summary>
/// The two WebSockets of one lane. Each socket has exactly one reader (its own handler's pump) and exactly one writer
/// (the partner handler's pump), which is what <see cref="WebSocket"/> allows without extra locking.
/// </summary>
internal sealed class TunnelLanePair : IDisposable
{
    private readonly object _lock = new();
    private readonly TaskCompletionSource<WebSocket> _client = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WebSocket> _host = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _bothPumpsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _abort = new();
    private bool _clientReserved;
    private bool _hostReserved;
    private int _pumpsDone;

    public CancellationToken AbortToken => _abort.Token;
    public Task BothPumpsDone => _bothPumpsDone.Task;

    public bool TryReserve(TunnelSide side)
    {
        lock (_lock)
        {
            ref var reserved = ref side == TunnelSide.Client ? ref _clientReserved : ref _hostReserved;
            if (reserved)
                return false;
            reserved = true;
            return true;
        }
    }

    /// <summary>Whether that side of the lane is taken (reserved or attached).</summary>
    public bool IsReserved(TunnelSide side)
    {
        lock (_lock)
            return side == TunnelSide.Client ? _clientReserved : _hostReserved;
    }

    public void Attach(TunnelSide side, WebSocket socket) => Slot(side).TrySetResult(socket);

    public Task<WebSocket> PartnerAsync(TunnelSide side) => Slot(side == TunnelSide.Client ? TunnelSide.Host : TunnelSide.Client).Task;

    public void MarkPumpDone()
    {
        if (Interlocked.Increment(ref _pumpsDone) == 2)
            _bothPumpsDone.TrySetResult();
    }

    public void Abort()
    {
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _abort.Dispose();

    private TaskCompletionSource<WebSocket> Slot(TunnelSide side) => side == TunnelSide.Client ? _client : _host;
}
