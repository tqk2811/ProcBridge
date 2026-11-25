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
/// <para>
/// Everything that sends, replaces or edits the entries of one remote path (close, flush, retry, recovery, rename and
/// delete on machine B) runs under that path's gate, one at a time and in order. A newer copy of an open file replaces
/// its entry not sent yet; an entry sent by the retry loop moves the version the open file compares with.
/// </para>
/// Does not own the <see cref="AgentClient"/> or the <see cref="FileCache"/>.
/// </summary>
public sealed class WriteBackQueue : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int SnapshotAttempts = 5;

    private readonly AgentClient _client;
    private readonly FileCache _cache;
    private readonly string _queueRoot;
    private readonly string _target;
    private readonly ConflictPolicy _policy;
    private readonly TimeSpan _retryDelay;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PathState> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, TokenState> _tokens = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
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
        // Change notifications and read-ahead must not replace a cache copy a writer still has open.
        cache.SetPinnedCheck(IsBusy);
    }

    /// <summary>
    /// Held handle on machine B (M8) a commit of (write token, remote path) goes through, 0 for none; token 0 when the
    /// commit is not tied to one writer (resend, end of session). Set once before use.
    /// </summary>
    public Func<ulong, string, ulong>? HoldIdLookup { get; set; }

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
                state.ConflictVersion = null;
            }
            state.OpenCount++;
        }
        ulong token = (ulong)Interlocked.Increment(ref _nextToken);
        _tokens[token] = new TokenState(state);
        return token;
    }

    /// <summary>
    /// Another process now holds <paramref name="token"/> too (a handle inherited by a child): the token, and the file
    /// open, stay until that holder closes it as well. Unknown tokens are ignored.
    /// </summary>
    public void Retain(ulong token)
    {
        lock (_lock)
        {
            if (!_tokens.TryGetValue(token, out TokenState? holder))
                return;
            holder.Count++;
            holder.State.OpenCount++;
        }
    }

    /// <summary>True while a writer has <paramref name="remotePath"/> open: the cache copy must not be replaced by a download.</summary>
    public bool IsBusy(string remotePath)
    {
        lock (_lock)
            return _paths.TryGetValue(remotePath, out PathState? state) && state.OpenCount > 0;
    }

    public async Task CloseAsync(ulong token, CancellationToken cancellationToken)
    {
        PathState state;
        bool final;
        lock (_lock)
        {
            if (!_tokens.TryGetValue(token, out TokenState? holder))
                return;
            if (--holder.Count == 0)
                _tokens.TryRemove(token, out _);
            state = holder.State;
            final = state.OpenCount == 1;
        }
        try
        {
            await SyncAsync(state, final, cancellationToken, token).ConfigureAwait(false);
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
        => _tokens.TryGetValue(token, out TokenState? holder) ? SyncAsync(holder.State, false, cancellationToken, token) : Task.CompletedTask;

    /// <summary>
    /// Takes the gates of <paramref name="paths"/> (in a fixed order): a rename or delete on machine B then cannot run
    /// while an entry of those paths is being sent, and the entries are updated before anything sends them again.
    /// </summary>
    public async Task<IDisposable> LockPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        SemaphoreSlim[] gates = paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(GetGate).ToArray();
        int taken = 0;
        try
        {
            for (; taken < gates.Length; taken++)
                await gates[taken].WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            for (int i = 0; i < taken; i++)
                gates[i].Release();
            throw;
        }
        return new GateRelease(gates);
    }

    /// <summary>
    /// Machine B renamed <paramref name="remotePath"/> (a file, or a directory with everything below it). Call under
    /// <see cref="LockPathsAsync"/> of both names. Open writers and unsent entries follow; those of a file the rename
    /// replaced are dropped.
    /// </summary>
    public void Renamed(string remotePath, string newRemotePath)
    {
        lock (_lock)
        {
            foreach (PathState state in _paths.Values.ToArray())
            {
                string? moved = Move(state.RemotePath, remotePath, newRemotePath);
                if (moved is null)
                    continue;
                // A file replaced by the rename keeps its writers, but they must not write over the new file.
                if (_paths.TryGetValue(moved, out PathState? replaced) && replaced != state)
                {
                    replaced.Deleted = true;
                    replaced.PendingEntryId = null;
                }
                _paths.Remove(state.RemotePath);
                state.RemotePath = moved;
                _paths[moved] = state;
            }
        }
        bool caseOnly = remotePath.Equals(newRemotePath, StringComparison.OrdinalIgnoreCase);
        foreach (WriteBackEntry entry in EnumerateEntries().ToArray())
        {
            if (Move(entry.RemotePath, remotePath, newRemotePath) is { } moved)
                SaveEntry(entry with { RemotePath = moved });
            else if (!caseOnly && Move(entry.RemotePath, newRemotePath, newRemotePath) is not null)
                DeleteEntry(entry.Id);
        }
    }

    /// <summary>
    /// Machine B deleted <paramref name="remotePath"/> (call under <see cref="LockPathsAsync"/>): open writers must not
    /// bring it back on close, and unsent entries of it are dropped.
    /// </summary>
    public void Deleted(string remotePath)
    {
        lock (_lock)
        {
            foreach (PathState state in _paths.Values)
            {
                if (Move(state.RemotePath, remotePath, remotePath) is not null)
                {
                    state.Deleted = true;
                    state.PendingEntryId = null;
                }
            }
        }
        foreach (WriteBackEntry entry in EnumerateEntries().ToArray())
        {
            if (Move(entry.RemotePath, remotePath, remotePath) is not null)
                DeleteEntry(entry.Id);
        }
    }

    /// <summary>Where <paramref name="path"/> goes when <paramref name="from"/> (and everything below it) becomes <paramref name="to"/>; null when unaffected.</summary>
    private static string? Move(string path, string from, string to)
        => path.Equals(from, StringComparison.OrdinalIgnoreCase) ? to
            : path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase) ? to + path[from.Length..]
            : null;

    private SemaphoreSlim GetGate(string remotePath) => _gates.GetOrAdd(remotePath, _ => new SemaphoreSlim(1, 1));

    /// <summary>Takes the gate of the state's current path (a rename while waiting moves it: wait for the new one).</summary>
    private async Task<SemaphoreSlim> EnterAsync(PathState state, CancellationToken cancellationToken)
    {
        while (true)
        {
            string path;
            lock (_lock)
                path = state.RemotePath;
            SemaphoreSlim gate = GetGate(path);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (state.RemotePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return gate;
            }
            gate.Release();
        }
    }

    /// <param name="final">Last close of the file: a file whose write-back conflicted still keeps its last copy.</param>
    /// <param name="token">Write token of the writer closing or flushing (its held handle carries the commit), 0 for none.</param>
    private async Task SyncAsync(PathState state, bool final, CancellationToken cancellationToken, ulong token = 0)
    {
        SemaphoreSlim gate = await EnterAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            string remotePath;
            FsVersion? expected;
            bool conflicted;
            FsVersion? conflictVersion;
            lock (_lock)
            {
                if (state.Deleted || (state.Conflicted && !final))
                    return;
                remotePath = state.RemotePath;
                expected = state.Base;
                conflicted = state.Conflicted;
                conflictVersion = state.ConflictVersion;
            }
            string localPath = _cache.GetLocalPath(remotePath);
            if (GetVersion(localPath) is not { } current)
                return;
            if (conflicted ? conflictVersion == current : expected == current)
                return;

            WriteBackEntry? entry = TryEnqueue(remotePath, localPath, expected, conflicted);
            if (entry is null)
                return;
            lock (_lock)
            {
                // The newer copy replaces the one not sent yet (same base, and it holds all of its changes).
                if (state.PendingEntryId is { } older && older != entry.Id)
                    DeleteEntry(older);
                state.PendingEntryId = entry.Id;
            }
            FsCommitResponse? response = await TryUploadAsync(entry, cancellationToken, token).ConfigureAwait(false);
            if (response is not null)
                Completed(state, entry, response);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>An entry of <paramref name="state"/> reached machine B (under the path's gate).</summary>
    private void Completed(PathState state, WriteBackEntry entry, FsCommitResponse response)
    {
        lock (_lock)
        {
            if (state.PendingEntryId == entry.Id)
                state.PendingEntryId = null;
            if (response.ConflictPath is not null)
            {
                state.Conflicted = true;
                state.ConflictVersion = entry.DataVersion;
                return;
            }
            state.Base = response.Entry.Version;
        }
        if (response.Entry.Version == entry.DataVersion && GetVersion(_cache.GetLocalPath(entry.RemotePath)) == entry.DataVersion)
            _cache.MarkContent(entry.RemotePath, response.Entry.Version);
    }

    private static FsVersion? GetVersion(string localPath)
    {
        var info = new FileInfo(localPath);
        return info.Exists ? new FsVersion { LastWriteTime = info.LastWriteTimeUtc.ToFileTimeUtc(), Size = info.Length } : null;
    }

    /// <summary>
    /// Saves the content and its entry in the queue folder; null when the cache copy cannot be read now. The copy is
    /// taken between two identical versions of the cache file (another handle may be writing it) and carries that
    /// version.
    /// </summary>
    private WriteBackEntry? TryEnqueue(string remotePath, string localPath, FsVersion? expectedVersion, bool keepBoth)
    {
        string id = Guid.NewGuid().ToString("N");
        string dataPath = GetDataPath(id);
        try
        {
            for (int attempt = 0; attempt < SnapshotAttempts; attempt++)
            {
                if (GetVersion(localPath) is not { } before)
                    break;
                // Other handles may still write the file: share everything, like the hooked process expects.
                using (var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var target = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.CopyTo(target);
                if (GetVersion(localPath) != before)
                    continue;
                File.SetLastWriteTimeUtc(dataPath, DateTime.FromFileTimeUtc(before.LastWriteTime));
                var entry = new WriteBackEntry
                {
                    Id = id,
                    Target = _target,
                    RemotePath = remotePath,
                    ExpectedVersion = expectedVersion,
                    DataVersion = before,
                    KeepBoth = keepBoth,
                    QueuedAt = DateTime.UtcNow,
                };
                SaveEntry(entry);
                return entry;
            }
            Trace.TraceWarning($"ProcBridge: write-back of '{remotePath}' postponed: the cache copy kept changing while copied.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A remaining handle denies read sharing: its own close tries again.
            Trace.TraceWarning($"ProcBridge: cannot queue write-back of '{remotePath}': {e.Message}");
        }
        TryDelete(dataPath);
        return null;
    }

    /// <summary>Writes the JSON of <paramref name="entry"/> (new, or replacing it after a rename) atomically.</summary>
    private void SaveEntry(WriteBackEntry entry)
    {
        string json = GetEntryPath(entry.Id);
        string temp = json + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry, JsonOptions));
        File.Move(temp, json, overwrite: true);
    }

    private void DeleteEntry(string id)
    {
        TryDelete(GetEntryPath(id));
        TryDelete(GetDataPath(id));
    }

    /// <summary>Uploads one entry (under its path's gate); null when it stays queued (connection) or was set aside (refused by machine B).</summary>
    private async Task<FsCommitResponse?> TryUploadAsync(WriteBackEntry entry, CancellationToken cancellationToken, ulong token = 0)
    {
        try
        {
            ConflictPolicy policy = entry.KeepBoth ? ConflictPolicy.KeepBoth : _policy;
            ulong holdId = HoldIdLookup?.Invoke(token, entry.RemotePath) ?? 0;
            FsCommitResponse response = await _client.UploadAsync(entry.RemotePath, GetDataPath(entry.Id), entry.Id, entry.ExpectedVersion, policy, cancellationToken, holdId).ConfigureAwait(false);
            DeleteEntry(entry.Id);
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
    }

    private static bool IsTransient(Exception e) => e switch
    {
        ProtocolStatusException p => p.IsConnectionFailure || p.Status is NtStatus.NetworkUnreachable or NtStatus.IoTimeout
            or NtStatus.UnexpectedNetworkError or NtStatus.InvalidNetworkResponse or NtStatus.Cancelled or NtStatus.SharingViolation
            // A range locked on machine B blocks an in-place commit (M8): it goes through once unlocked.
            or NtStatus.LockNotGranted,
        OperationCanceledException or IOException or TimeoutException or System.Net.Sockets.SocketException => true,
        _ => false,
    };

    /// <summary>Uploads every queued entry of this target, oldest first, each under its path's gate; returns how many went through.</summary>
    public async Task<int> ResendPendingAsync(CancellationToken cancellationToken)
    {
        int sent = 0;
        foreach (WriteBackEntry listed in EnumerateEntries().OrderBy(x => x.QueuedAt).ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            using (await LockPathsAsync([listed.RemotePath], cancellationToken).ConfigureAwait(false))
            {
                // Sent, replaced, renamed or dropped while waiting for the gate: read it again.
                WriteBackEntry? entry = ReadEntry(GetEntryPath(listed.Id));
                if (entry is null || !entry.RemotePath.Equals(listed.RemotePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(GetDataPath(entry.Id)))
                {
                    TryDelete(GetEntryPath(entry.Id));
                    continue;
                }
                FsCommitResponse? response = await TryUploadAsync(entry, cancellationToken).ConfigureAwait(false);
                if (response is null)
                    continue;
                sent++;
                PathState? owner;
                lock (_lock)
                    owner = _paths.Values.FirstOrDefault(x => x.PendingEntryId == entry.Id);
                if (owner is not null)
                    Completed(owner, entry, response);
                else if (!IsBusy(entry.RemotePath))
                    _cache.ForgetContent(entry.RemotePath);
            }
        }
        return sent;
    }

    private IEnumerable<WriteBackEntry> EnumerateEntries()
    {
        foreach (string path in Directory.EnumerateFiles(_queueRoot, "*.json"))
        {
            if (ReadEntry(path) is { } entry)
                yield return entry;
        }
    }

    /// <summary>The entry saved at <paramref name="path"/> when it belongs to this target; null otherwise or when gone.</summary>
    private WriteBackEntry? ReadEntry(string path)
    {
        WriteBackEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<WriteBackEntry>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        return entry is not null && entry.Target == _target && Path.GetFileNameWithoutExtension(path) == entry.Id ? entry : null;
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
    /// Close (nor does a child holding an inherited handle with a token). Runs when the session ends (the whole process
    /// tree is gone), as the last copy of each file.
    /// </summary>
    public async Task SyncOpenAsync(CancellationToken cancellationToken)
    {
        PathState[] open;
        lock (_lock)
            open = _paths.Values.Where(x => x.OpenCount > 0).ToArray();
        foreach (PathState state in open)
            await SyncAsync(state, true, cancellationToken).ConfigureAwait(false);
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

        /// <summary>Holders of write tokens on the file (each process holding a token counts once).</summary>
        public int OpenCount { get; set; }

        public bool Deleted { get; set; }

        /// <summary>A write-back conflicted: later flushes leave machine B alone; the last close keeps its copy beside it.</summary>
        public bool Conflicted { get; set; }

        /// <summary>Version of the cache copy last saved beside machine B's file after a conflict.</summary>
        public FsVersion? ConflictVersion { get; set; }

        /// <summary>Queue entry of this file not sent yet (changed under the path's gate and the queue lock).</summary>
        public string? PendingEntryId { get; set; }
    }

    /// <summary>A write token and the number of processes holding it (a handle inherited by a child adds one).</summary>
    private sealed class TokenState(PathState state)
    {
        public PathState State { get; } = state;

        /// <summary>Under the queue lock.</summary>
        public int Count { get; set; } = 1;
    }

    private sealed class GateRelease(SemaphoreSlim[] gates) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            foreach (SemaphoreSlim gate in gates)
                gate.Release();
        }
    }
}
