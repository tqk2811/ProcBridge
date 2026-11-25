using System.Diagnostics;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Broker;

/// <summary>
/// Handles machine B keeps for opens with a write token (M8): byte range locks of the hooked process (NtLockFile) are
/// taken on them, and with <c>holdShareMode</c> each writer gets one at open time with its share mode, so a user of
/// machine B cannot open the file in a way the open on machine A forbids (and machine B's users holding it make the
/// open fail with a sharing violation). They live on one dedicated connection: when it breaks, machine B closes them
/// (and their locks). A token that had locks then is marked lost: its later Lock and Unlock fail with
/// <see cref="NtStatus.FileLockConflict"/> instead of silently locking a fresh handle; with <c>holdShareMode</c> every
/// hold is opened again on a new connection at once, to keep the share mode. A token held by several processes (a
/// handle inherited by a child, <see cref="Retain"/>) is released with its last Close or when the last of those
/// processes goes away. Thread safe. Does not own the <see cref="AgentClient"/>.
/// </summary>
public sealed class RemoteHolds : IAsyncDisposable
{
    private readonly AgentClient _client;
    private readonly bool _holdShareMode;
    private readonly TimeSpan _lockWait;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<ulong, Hold> _holds = [];
    private readonly Dictionary<long, int> _clientConnections = [];
    private ProtocolConnection? _connection;
    private bool _disposed;

    /// <param name="holdShareMode">Open the handle on machine B when the writer opens (share mode), not only at its first lock.</param>
    /// <param name="lockWait">Longest a lock that may wait (not FailImmediately) waits on machine B.</param>
    public RemoteHolds(AgentClient client, bool holdShareMode, TimeSpan lockWait)
    {
        _client = client;
        _holdShareMode = holdShareMode;
        _lockWait = lockWait;
    }

    /// <summary>Handles currently held on machine B, for tests.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _holds.Values.Count(x => x.HandleId != 0);
        }
    }

    /// <summary>A writer opened <paramref name="remotePath"/> with <paramref name="token"/>: hold it on machine B (share mode).</summary>
    public async Task OpenAsync(ulong token, string remotePath, uint shareAccess, long clientId, CancellationToken cancellationToken)
    {
        var hold = new Hold(remotePath, shareAccess);
        hold.Holders.Add(clientId);
        lock (_lock)
            _holds[token] = hold;
        if (!_holdShareMode)
            return;
        try
        {
            await EnsureHandleAsync(hold, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
                _holds.Remove(token);
            throw;
        }
    }

    /// <summary>
    /// Id on machine B of the handle a commit of <paramref name="remotePath"/> should write through: the one of
    /// <paramref name="token"/>, else any handle held on that path, else 0.
    /// </summary>
    public ulong GetHoldId(ulong token, string remotePath)
    {
        lock (_lock)
        {
            if (_holds.TryGetValue(token, out Hold? own) && own.HandleId != 0 && own.RemotePath.Equals(remotePath, StringComparison.OrdinalIgnoreCase))
                return own.HandleId;
            return _holds.Values.FirstOrDefault(x => x.HandleId != 0 && x.RemotePath.Equals(remotePath, StringComparison.OrdinalIgnoreCase))?.HandleId ?? 0;
        }
    }

    /// <summary>NtLockFile on a handle holding <paramref name="token"/>.</summary>
    public async Task LockAsync(ulong token, long offset, long length, bool exclusive, bool failImmediately, CancellationToken cancellationToken)
    {
        Hold hold = Get(token);
        ThrowIfLost(hold);
        ulong handleId = await EnsureHandleAsync(hold, cancellationToken).ConfigureAwait(false);
        var request = new FsLockRequest
        {
            HandleId = handleId,
            Offset = offset,
            Length = length,
            Exclusive = exclusive,
            WaitMilliseconds = failImmediately ? 0 : (int)_lockWait.TotalMilliseconds,
        };
        await SendAsync(hold, MessageType.FsLock, request, cancellationToken).ConfigureAwait(false);
        lock (_lock)
            hold.LockCount++;
    }

    /// <summary>NtUnlockFile on a handle holding <paramref name="token"/>.</summary>
    public async Task UnlockAsync(ulong token, long offset, long length, CancellationToken cancellationToken)
    {
        Hold hold = Get(token);
        ThrowIfLost(hold);
        if (hold.HandleId == 0)
            throw new ProtocolStatusException(NtStatus.RangeNotLocked, "Nothing is locked on machine B.");
        await SendAsync(hold, MessageType.FsUnlock, new FsLockRequest { HandleId = hold.HandleId, Offset = offset, Length = length }, cancellationToken).ConfigureAwait(false);
        lock (_lock)
            hold.LockCount = Math.Max(0, hold.LockCount - 1);
    }

    /// <summary>Process <paramref name="clientId"/> now holds <paramref name="token"/> too (a handle inherited by a child).</summary>
    public void Retain(ulong token, long clientId)
    {
        lock (_lock)
        {
            if (_holds.TryGetValue(token, out Hold? hold))
                hold.Holders.Add(clientId);
        }
    }

    /// <summary>
    /// Process <paramref name="clientId"/> closed its last handle holding <paramref name="token"/>: with the last holder
    /// machine B lets the file (and its locks) go.
    /// </summary>
    public async Task ReleaseAsync(ulong token, long clientId)
    {
        Hold? hold;
        lock (_lock)
        {
            if (!_holds.TryGetValue(token, out hold))
                return;
            // A holder unknown here (handle passed another way) still closes one hold of the token.
            if (!hold.Holders.Remove(clientId) && hold.Holders.Count > 0)
                hold.Holders.RemoveAt(0);
            if (hold.Holders.Count > 0)
                return;
            _holds.Remove(token);
        }
        await CloseAsync(hold).ConfigureAwait(false);
    }

    /// <summary>A pipe connection of hooked process <paramref name="clientId"/> opened.</summary>
    public void ClientConnected(long clientId)
    {
        lock (_lock)
            _clientConnections[clientId] = _clientConnections.GetValueOrDefault(clientId) + 1;
    }

    /// <summary>
    /// A pipe connection of <paramref name="clientId"/> closed; with its last one the process is gone (or crashed) and
    /// the holds no other process holds any more are released, like the system releases the locks of a dead process.
    /// </summary>
    public void ClientDisconnected(long clientId)
    {
        List<Hold> released = [];
        lock (_lock)
        {
            int left = _clientConnections.GetValueOrDefault(clientId) - 1;
            if (left > 0)
            {
                _clientConnections[clientId] = left;
                return;
            }
            _clientConnections.Remove(clientId);
            foreach (KeyValuePair<ulong, Hold> pair in _holds.ToArray())
            {
                if (pair.Value.Holders.RemoveAll(x => x == clientId) == 0 || pair.Value.Holders.Count > 0)
                    continue;
                _holds.Remove(pair.Key);
                released.Add(pair.Value);
            }
        }
        foreach (Hold hold in released)
            _ = CloseAsync(hold);
    }

    private Hold Get(ulong token)
    {
        lock (_lock)
        {
            return _holds.TryGetValue(token, out Hold? hold)
                ? hold
                : throw new ProtocolStatusException(NtStatus.InvalidHandle, "The handle has no write token.");
        }
    }

    private static void ThrowIfLost(Hold hold)
    {
        if (hold.Lost)
            throw new ProtocolStatusException(NtStatus.FileLockConflict, $"The locks on '{hold.RemotePath}' were lost with the connection to machine B.");
    }

    /// <summary>
    /// The connection holding the files broke: machine B closed every handle and lock on it. Tokens that had locks are
    /// marked lost; with <c>holdShareMode</c> the holds are opened again on a new connection to keep their share mode.
    /// </summary>
    private void OnConnectionLost(ProtocolConnection connection)
    {
        Hold[] reopen;
        lock (_lock)
        {
            Hold[] affected = _holds.Values.Where(x => ReferenceEquals(x.Connection, connection)).ToArray();
            foreach (Hold hold in affected)
            {
                if (hold.LockCount > 0 && !hold.Lost)
                {
                    hold.Lost = true;
                    Trace.TraceWarning($"ProcBridge: the connection holding '{hold.RemotePath}' on machine B broke; its byte range locks are lost.");
                }
            }
            if (_disposed || !_holdShareMode)
                return;
            reopen = affected;
        }
        if (reopen.Length > 0)
            _ = ReopenAsync(reopen);
    }

    private async Task ReopenAsync(Hold[] holds)
    {
        foreach (Hold hold in holds)
        {
            try
            {
                await EnsureHandleAsync(hold, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Trace.TraceWarning($"ProcBridge: could not hold '{hold.RemotePath}' on machine B again: {e.Message}");
            }
        }
    }

    /// <summary>The handle id of <paramref name="hold"/> on the current connection, opening it when needed.</summary>
    private async Task<ulong> EnsureHandleAsync(Hold hold, CancellationToken cancellationToken)
    {
        await hold.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtocolConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (hold.HandleId != 0 && ReferenceEquals(hold.Connection, connection))
                return hold.HandleId;
            var request = new FsOpenHandleRequest { Path = hold.RemotePath, ShareAccess = hold.ShareAccess, Write = true };
            Frame frame = await connection.SendAsync(MessageType.FsOpenHandle, ProtocolSerializer.Serialize(request), default, cancellationToken).ConfigureAwait(false);
            hold.HandleId = ProtocolSerializer.Deserialize<FsHandleResponse>(frame.Header).HandleId;
            hold.Connection = connection;
            return hold.HandleId;
        }
        finally
        {
            hold.Gate.Release();
        }
    }

    private async Task SendAsync(Hold hold, MessageType type, FsLockRequest request, CancellationToken cancellationToken)
    {
        ProtocolConnection? connection = hold.Connection;
        if (connection is null || !connection.IsAlive)
            throw new ProtocolStatusException(NtStatus.UnexpectedNetworkError, "The connection holding the file on machine B was lost.");
        await connection.SendAsync(type, ProtocolSerializer.Serialize(request), default, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CloseAsync(Hold hold)
    {
        ProtocolConnection? connection = hold.Connection;
        if (hold.HandleId == 0 || connection is null || !connection.IsAlive)
            return;
        try
        {
            await connection.SendAsync(MessageType.FsCloseHandle, ProtocolSerializer.Serialize(new FsLockRequest { HandleId = hold.HandleId }), default, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The connection broke: machine B closed the handle with it.
        }
        hold.HandleId = 0;
    }

    private async Task<ProtocolConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ProtocolConnection? current = Volatile.Read(ref _connection);
        if (current is { IsAlive: true })
            return current;
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsAlive: true })
                return _connection;
            if (_connection is not null)
                await _connection.DisposeAsync().ConfigureAwait(false);
            ProtocolConnection opened = await _client.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            _connection = opened;
            _ = opened.Closed.ContinueWith(_ => OnConnectionLost(opened), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return opened;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ProtocolConnection? connection;
        lock (_lock)
            _disposed = true;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            connection = _connection;
            _connection = null;
        }
        finally
        {
            _connectLock.Release();
        }
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Hold(string remotePath, uint shareAccess)
    {
        public string RemotePath { get; } = remotePath;
        public uint ShareAccess { get; } = shareAccess;
        /// <summary>Processes holding the token, once per Open or Retain (under the holds lock).</summary>
        public List<long> Holders { get; } = [];
        /// <summary>Locks granted on machine B and not unlocked yet (under the holds lock).</summary>
        public int LockCount { get; set; }
        /// <summary>The connection broke while locks were held: the process's locks are gone.</summary>
        public bool Lost { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ulong HandleId { get; set; }
        public ProtocolConnection? Connection { get; set; }
    }
}
