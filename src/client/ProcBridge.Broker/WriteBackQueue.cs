using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Write-back of cache copies to machine B (plan 4.4, 4.5 and 10). An open that may change a file's content gets a
/// write token; when a handle with a token is closed or flushed, a cache copy that no longer matches the version it was
/// derived from (size or last write time) is uploaded whole and committed against that version.
/// <para>
/// Before an upload starts, the content is copied into the queue folder with a small JSON entry, so a Broker that dies
/// or loses the Agent mid-way sends it again: at the next start (<see cref="StartRecovery"/>) and, while running, from
/// a retry loop. A conflict (machine B changed meanwhile) follows <see cref="ConflictPolicy"/>.
/// </para>
/// Does not own the <see cref="AgentClient"/> or the <see cref="FileCache"/>.
/// </summary>
public sealed class WriteBackQueue : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly AgentClient _client;
    private readonly FileCache _cache;
    private readonly string _queueRoot;
    private readonly string _target;
    private readonly ConflictPolicy _policy;
    private readonly TimeSpan _retryDelay;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PathState> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, PathState> _tokens = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<WriteConflict> _conflicts = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _retryLoop = Task.CompletedTask;
    private long _nextToken;
    private int _retryNeeded;

    /// <param name="target">Agent and share (<c>host:port/share</c>): queue entries of other targets are left alone.</param>
    public WriteBackQueue(AgentClient client, FileCache cache, string queueRoot, string target, ConflictPolicy policy, TimeSpan retryDelay)
    {
        _client = client;
        _cache = cache;
        _queueRoot = Path.GetFullPath(queueRoot);
        _target = target;
        _policy = policy;
        _retryDelay = retryDelay;
        Directory.CreateDirectory(_queueRoot);
    }

    /// <summary>Conflicts seen by this Broker, oldest first.</summary>
    public IReadOnlyList<WriteConflict> Conflicts => _conflicts.ToArray();

    /// <summary>Resend of the entries left by an earlier run; completes when all were tried once.</summary>
    public Task Recovery { get; private set; } = Task.CompletedTask;

    /// <summary>Starts the resend of leftover entries and the retry loop.</summary>
    public void StartRecovery()
    {
        Recovery = Task.Run(() => ResendPendingAsync(_stopping.Token));
        _retryLoop = Task.Run(RetryLoopAsync);
    }

    /// <summary>Number of queue entries of this target still waiting (not uploaded yet).</summary>
    public int PendingCount => EnumerateEntries().Count();

    /// <summary>Registers an open that may change <paramref name="remotePath"/>; returns its write token.</summary>
    /// <param name="baseVersion">Version of machine B the cache copy holds (or will hold after the open's truncation).</param>
    public ulong Open(string remotePath, FsVersion? baseVersion)
    {
        PathState state;
        lock (_lock)
        {
            if (!_paths.TryGetValue(remotePath, out state!))
            {
                state = new PathState { RemotePath = remotePath };
                _paths.Add(remotePath, state);
            }
            if (state.OpenCount == 0)
            {
                state.Base = baseVersion;
                state.Deleted = false;
                state.Conflicted = false;
            }
            state.OpenCount++;
        }
        ulong token = (ulong)Interlocked.Increment(ref _nextToken);
        _tokens[token] = state;
        return token;
    }

    /// <summary>True while a writer has <paramref name="remotePath"/> open: the cache copy must not be replaced by a download.</summary>
    public bool IsBusy(string remotePath)
    {
        lock (_lock)
            return _paths.TryGetValue(remotePath, out PathState? state) && state.OpenCount > 0;
    }

    public async Task CloseAsync(ulong token, CancellationToken cancellationToken)
    {
        if (!_tokens.TryRemove(token, out PathState? state))
            return;
        try
        {
            await SyncAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (--state.OpenCount == 0)
                {
                    if (state.Conflicted)
                        _cache.ForgetContent(state.RemotePath);
                    if (_paths.TryGetValue(state.RemotePath, out PathState? current) && current == state)
                        _paths.Remove(state.RemotePath);
                }
            }
        }
    }

    public Task FlushAsync(ulong token, CancellationToken cancellationToken)
        => _tokens.TryGetValue(token, out PathState? state) ? SyncAsync(state, cancellationToken) : Task.CompletedTask;

    /// <summary>Machine B renamed <paramref name="remotePath"/> (a file, or a directory with everything below it).</summary>
    public void Renamed(string remotePath, string newRemotePath)
    {
        lock (_lock)
        {
            string prefix = remotePath + "/";
            foreach (PathState state in _paths.Values.ToArray())
            {
                string? moved = state.RemotePath.Equals(remotePath, StringComparison.OrdinalIgnoreCase) ? newRemotePath
                    : state.RemotePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? newRemotePath + state.RemotePath[remotePath.Length..]
                    : null;
                if (moved is null)
                    continue;
                // A file replaced by the rename keeps its writers, but they must not write over the new file.
                if (_paths.TryGetValue(moved, out PathState? replaced) && replaced != state)
                    replaced.Deleted = true;
                _paths.Remove(state.RemotePath);
                state.RemotePath = moved;
                _paths[moved] = state;
            }
        }
    }

    /// <summary>Machine B deleted <paramref name="remotePath"/>: open writers must not bring it back on close.</summary>
    public void Deleted(string remotePath)
    {
        lock (_lock)
        {
            string prefix = remotePath + "/";
            foreach (PathState state in _paths.Values)
            {
                if (state.RemotePath.Equals(remotePath, StringComparison.OrdinalIgnoreCase) || state.RemotePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    state.Deleted = true;
            }
        }
    }

    /// <summary>Times or attributes changed on machine B through the Broker: the version open writers compare with moves too.</summary>
    public void InfoChanged(string remotePath, FsEntry entry)
    {
        lock (_lock)
        {
            if (_paths.TryGetValue(remotePath, out PathState? state) && state.Base is not null)
                state.Base = entry.Version;
        }
    }

    private async Task SyncAsync(PathState state, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string remotePath;
            lock (_lock)
            {
                if (state.Deleted || state.Conflicted)
                    return;
                remotePath = state.RemotePath;
            }
            var info = new FileInfo(_cache.GetLocalPath(remotePath));
            if (!info.Exists)
                return;
            var current = new FsVersion { LastWriteTime = info.LastWriteTimeUtc.ToFileTimeUtc(), Size = info.Length };
            if (state.Base == current)
                return;

            WriteBackEntry? entry = TryEnqueue(remotePath, info.FullName, state.Base);
            if (entry is null)
                return;
            FsCommitResponse? response = await TryUploadAsync(entry, cancellationToken).ConfigureAwait(false);
            if (response is null)
                return;
            if (response.ConflictPath is not null)
            {
                state.Conflicted = true;
                return;
            }
            state.Base = response.Entry.Version;
            if (response.Entry.Version == current)
                _cache.MarkContent(remotePath, current);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Saves the content and its entry in the queue folder; null when the cache copy cannot be read now.</summary>
    private WriteBackEntry? TryEnqueue(string remotePath, string localPath, FsVersion? expectedVersion)
    {
        var entry = new WriteBackEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Target = _target,
            RemotePath = remotePath,
            ExpectedVersion = expectedVersion,
            QueuedAt = DateTime.UtcNow,
        };
        string dataPath = GetDataPath(entry.Id);
        try
        {
            // Other handles may still write the file: share everything, like the hooked process expects.
            using (var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            File.SetLastWriteTimeUtc(dataPath, File.GetLastWriteTimeUtc(localPath));
            string json = GetEntryPath(entry.Id);
            string temp = json + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, JsonOptions));
            File.Move(temp, json);
            return entry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A remaining handle denies read sharing: its own close tries again.
            Trace.TraceWarning($"ProcBridge: cannot queue write-back of '{remotePath}': {e.Message}");
            TryDelete(dataPath);
            return null;
        }
    }

    /// <summary>Uploads one entry; null when it stays queued (connection) or was set aside (refused by machine B).</summary>
    private async Task<FsCommitResponse?> TryUploadAsync(WriteBackEntry entry, CancellationToken cancellationToken)
    {
        if (!_inFlight.TryAdd(entry.Id, 0))
            return null;
        try
        {
            FsCommitResponse response = await _client.UploadAsync(entry.RemotePath, GetDataPath(entry.Id), entry.ExpectedVersion, _policy, cancellationToken).ConfigureAwait(false);
            TryDelete(GetEntryPath(entry.Id));
            TryDelete(GetDataPath(entry.Id));
            _cache.Update(entry.RemotePath, response.Entry);
            if (response.ConflictPath is not null)
            {
                var conflict = new WriteConflict(entry.RemotePath, response.ConflictPath);
                _conflicts.Enqueue(conflict);
                _cache.Invalidate(response.ConflictPath);
                Trace.TraceWarning($"ProcBridge: '{entry.RemotePath}' changed on machine B; machine A's version was saved as '{response.ConflictPath}'.");
            }
            return response;
        }
        catch (Exception e) when (IsTransient(e))
        {
            Interlocked.Exchange(ref _retryNeeded, 1);
            Trace.TraceWarning($"ProcBridge: write-back of '{entry.RemotePath}' postponed: {e.Message}");
            return null;
        }
        catch (ProtocolStatusException e)
        {
            // Machine B refused (access denied, folder gone...): resending would fail the same way. Keep the content.
            Trace.TraceError($"ProcBridge: write-back of '{entry.RemotePath}' refused by machine B ({e.Status}); kept as {GetDataPath(entry.Id)}.");
            TryMove(GetEntryPath(entry.Id), GetEntryPath(entry.Id) + ".failed");
            return null;
        }
        finally
        {
            _inFlight.TryRemove(entry.Id, out _);
        }
    }

    private static bool IsTransient(Exception e) => e switch
    {
        ProtocolStatusException p => p.IsConnectionFailure || p.Status is NtStatus.NetworkUnreachable or NtStatus.IoTimeout
            or NtStatus.UnexpectedNetworkError or NtStatus.InvalidNetworkResponse or NtStatus.Cancelled or NtStatus.SharingViolation,
        OperationCanceledException or IOException or TimeoutException or System.Net.Sockets.SocketException => true,
        _ => false,
    };

    /// <summary>Uploads every queued entry of this target, oldest first; returns how many went through.</summary>
    public async Task<int> ResendPendingAsync(CancellationToken cancellationToken)
    {
        int sent = 0;
        foreach (WriteBackEntry entry in EnumerateEntries().OrderBy(x => x.QueuedAt))
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            if (!File.Exists(GetDataPath(entry.Id)))
            {
                TryDelete(GetEntryPath(entry.Id));
                continue;
            }
            if (await TryUploadAsync(entry, cancellationToken).ConfigureAwait(false) is not null)
            {
                _cache.ForgetContent(entry.RemotePath);
                sent++;
            }
        }
        return sent;
    }

    private IEnumerable<WriteBackEntry> EnumerateEntries()
    {
        foreach (string path in Directory.EnumerateFiles(_queueRoot, "*.json"))
        {
            WriteBackEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<WriteBackEntry>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }
            if (entry is not null && entry.Target == _target && Path.GetFileNameWithoutExtension(path) == entry.Id)
                yield return entry;
        }
    }

    private async Task RetryLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_retryDelay, _stopping.Token).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _retryNeeded, 0) == 1)
                    await ResendPendingAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Trace.TraceError($"ProcBridge: write-back retry failed: {e.Message}");
            }
        }
    }

    private string GetDataPath(string id) => Path.Combine(_queueRoot, id + ".data");

    private string GetEntryPath(string id) => Path.Combine(_queueRoot, id + ".json");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryMove(string path, string destination)
    {
        try
        {
            File.Move(path, destination, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes back every file still open for writing: a process that exits without closing its handles never sends
    /// Close. Runs when the session ends (the whole process tree is gone).
    /// </summary>
    public async Task SyncOpenAsync(CancellationToken cancellationToken)
    {
        PathState[] open;
        lock (_lock)
            open = _paths.Values.Where(x => x.OpenCount > 0).ToArray();
        foreach (PathState state in open)
            await SyncAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        try
        {
            await Task.WhenAll(_retryLoop, Recovery).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stopping.Dispose();
    }

    /// <summary>Write state of one remote file while handles with write tokens are open on it.</summary>
    private sealed class PathState
    {
        /// <summary>Current share path (renames move it); read and written under the queue lock.</summary>
        public required string RemotePath { get; set; }

        /// <summary>Version of machine B the cache copy was derived from; null when unknown (no conflict check).</summary>
        public FsVersion? Base { get; set; }

        public int OpenCount { get; set; }

        public bool Deleted { get; set; }

        /// <summary>A write-back conflicted: later closes leave machine B alone until every writer closed.</summary>
        public bool Conflicted { get; set; }

        /// <summary>One sync at a time per file.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
