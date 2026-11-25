namespace ProcBridge.Protocol.Transport;

/// <summary>
/// Send lock of one frame channel that lets urgent frames (requests, metadata answers, pings: small bodies) go before
/// bulk frames (file data) waiting for the same connection, so a large download does not hold back metadata on the
/// connection it shares (plan section 7). Waiters of one class keep their order. After
/// <see cref="MaxUrgentInARow"/> urgent grants in a row while bulk frames wait, one bulk frame goes, so data is never
/// starved.
/// </summary>
internal sealed class FrameSendGate
{
    /// <summary>Urgent frames granted in a row before a waiting bulk frame gets its turn.</summary>
    public const int MaxUrgentInARow = 16;

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _urgent = new();
    private readonly LinkedList<Waiter> _bulk = new();
    private bool _held;
    private int _urgentInARow;

    /// <summary>Frames waiting for the gate (diagnostics and tests).</summary>
    public int WaitingCount
    {
        get
        {
            lock (_lock)
                return _urgent.Count + _bulk.Count;
        }
    }

    public Task WaitAsync(bool urgent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<Waiter> node;
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }
            node = (urgent ? _urgent : _bulk).AddLast(new Waiter());
        }
        if (cancellationToken.CanBeCanceled)
            node.Value.Registration = cancellationToken.Register(() => Cancel(node, cancellationToken));
        return node.Value.Completion.Task;
    }

    private void Cancel(LinkedListNode<Waiter> node, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // Already granted: the owner releases the gate as usual.
            if (node.List is null)
                return;
            node.List.Remove(node);
        }
        node.Value.Completion.TrySetCanceled(cancellationToken);
    }

    public void Release()
    {
        Waiter next;
        lock (_lock)
        {
            LinkedList<Waiter> from;
            if (_urgent.Count > 0 && (_bulk.Count == 0 || _urgentInARow < MaxUrgentInARow))
            {
                from = _urgent;
                _urgentInARow = _bulk.Count == 0 ? 0 : _urgentInARow + 1;
            }
            else if (_bulk.Count > 0)
            {
                from = _bulk;
                _urgentInARow = 0;
            }
            else
            {
                _held = false;
                return;
            }
            next = from.First!.Value;
            from.RemoveFirst();
        }
        next.Registration.Unregister();
        next.Completion.TrySetResult();
    }

    private sealed class Waiter
    {
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }
}
