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
/// (and their locks); a later lock opens a new one. Released when the token closes or the hooked process goes away.
/// Thread safe. Does not own the <see cref="AgentClient"/>.
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
        var hold = new Hold(remotePath, shareAccess, clientId);
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
    }

    /// <summary>NtUnlockFile on a handle holding <paramref name="token"/>.</summary>
    public async Task UnlockAsync(ulong token, long offset, long length, CancellationToken cancellationToken)
    {
        Hold hold = Get(token);
        if (hold.HandleId == 0)
            throw new ProtocolStatusException(NtStatus.RangeNotLocked, "Nothing is locked on machine B.");
        await SendAsync(hold, MessageType.FsUnlock, new FsLockRequest { HandleId = hold.HandleId, Offset = offset, Length = length }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The last handle holding <paramref name="token"/> was closed: machine B lets the file (and its locks) go.</summary>
    public async Task ReleaseAsync(ulong token)
    {
        Hold? hold;
        lock (_lock)
        {
            if (!_holds.Remove(token, out hold))
                return;
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
    /// its holds are released, like the system releases the locks of a dead process.
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
            foreach (KeyValuePair<ulong, Hold> pair in _holds.Where(x => x.Value.ClientId == clientId).ToArray())
            {
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
            _connection = await _client.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ProtocolConnection? connection;
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

    private sealed class Hold(string remotePath, uint shareAccess, long clientId)
    {
        public string RemotePath { get; } = remotePath;
        public uint ShareAccess { get; } = shareAccess;
        public long ClientId { get; } = clientId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ulong HandleId { get; set; }
        public ProtocolConnection? Connection { get; set; }
    }
}
