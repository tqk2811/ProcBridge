using System.Collections.Concurrent;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Local cache of one share (plan 4.4 and 10). Stat and list results live for a short TTL; file contents are
/// downloaded whole, get machine B's times and attributes, and are reused while the remote version
/// (last write time + size) stays the same. Concurrent requests for the same file share one download.
/// On a writable share (M3) cache copies keep machine B's attributes (a read-only share forces read-only), and the
/// write operations below change machine B first, then bring the cache and its metadata in line.
/// Does not own the <see cref="AgentClient"/>.
/// </summary>
public sealed class FileCache
{
    private const int MaxVersionRetries = 2;

    /// <summary>Attributes copied onto cache files; the others (directory, sparse, reparse...) describe storage.</summary>
    private const FileAttributes MirroredAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    private readonly AgentClient _client;
    private readonly string _cacheRoot;
    private readonly long _ttlMilliseconds;
    private readonly ConcurrentDictionary<string, Timed<FsEntry>> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timed<IReadOnlyList<FsEntry>>> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FsVersion> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedItem>>> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<FsEntry>>> _pendingStats = new(StringComparer.OrdinalIgnoreCase);
    private Lazy<Task<bool>> _writable;

    public FileCache(AgentClient client, string cacheRoot, TimeSpan metadataTtl)
    {
        _client = client;
        _cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        _ttlMilliseconds = (long)metadataTtl.TotalMilliseconds;
        _writable = new Lazy<Task<bool>>(() => client.IsWritableAsync(CancellationToken.None));
        Directory.CreateDirectory(_cacheRoot);
        DeleteLeftoverTempFiles();
    }

    /// <summary>Removes <c>*.pbdl-*.tmp</c> files that a crashed Broker left behind in earlier runs.</summary>
    private void DeleteLeftoverTempFiles()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string path in Directory.EnumerateFiles(_cacheRoot, "*.pbdl-*.tmp", options))
            TryDelete(path);
    }

    public string CacheRoot => _cacheRoot;

    /// <summary>True when the share accepts writes; asked once per Broker (Hello answer).</summary>
    public async Task<bool> IsWritableAsync(CancellationToken cancellationToken)
    {
        Lazy<Task<bool>> current = _writable;
        try
        {
            return await current.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (current.Value.IsFaulted)
        {
            // A failed first connect must not stick: ask again next time.
            Interlocked.CompareExchange(ref _writable, new Lazy<Task<bool>>(() => _client.IsWritableAsync(CancellationToken.None)), current);
            throw;
        }
    }

    public async Task<FsEntry> StatAsync(string remotePath, CancellationToken cancellationToken)
    {
        if (_stats.TryGetValue(remotePath, out Timed<FsEntry>? cached) && !cached.IsExpired)
            return cached.Value;

        // Build tools stat the same file from many threads at once: share one request per path.
        Lazy<Task<FsEntry>> pending = _pendingStats.GetOrAdd(remotePath, key => new Lazy<Task<FsEntry>>(() => FetchStatAsync(key)));
        try
        {
            return await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending.Value.IsCompleted)
                _pendingStats.TryRemove(new KeyValuePair<string, Lazy<Task<FsEntry>>>(remotePath, pending));
        }
    }

    private async Task<FsEntry> FetchStatAsync(string remotePath)
    {
        await Task.Yield();
        FsEntry entry = await _client.StatAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
        _stats[remotePath] = new Timed<FsEntry>(entry, Environment.TickCount64 + _ttlMilliseconds);
        return entry;
    }

    public async Task<IReadOnlyList<FsEntry>> ListAsync(string remotePath, CancellationToken cancellationToken)
    {
        if (_lists.TryGetValue(remotePath, out Timed<IReadOnlyList<FsEntry>>? cached) && !cached.IsExpired)
            return cached.Value;
        IReadOnlyList<FsEntry> entries = await _client.ListAsync(remotePath, cancellationToken).ConfigureAwait(false);
        long expires = Environment.TickCount64 + _ttlMilliseconds;
        _lists[remotePath] = new Timed<IReadOnlyList<FsEntry>>(entries, expires);
        // A listing also refreshes the children's metadata, saving one stat per file for build tools.
        string prefix = remotePath.Length == 0 ? string.Empty : remotePath + "/";
        foreach (FsEntry entry in entries)
            _stats[prefix + entry.Name] = new Timed<FsEntry>(entry, expires);
        return entries;
    }

    /// <summary>Makes sure <paramref name="remotePath"/> is in the cache with the current remote version.</summary>
    public async Task<CachedItem> EnsureAsync(string remotePath, CancellationToken cancellationToken)
    {
        FsEntry entry = await StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
        string localPath = GetLocalPath(remotePath);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(localPath);
            return new CachedItem(entry, localPath);
        }

        if (_versions.TryGetValue(remotePath, out FsVersion version) && version == entry.Version && MatchesVersion(localPath, version))
            return new CachedItem(entry, localPath);

        Lazy<Task<CachedItem>> download = _downloads.GetOrAdd(remotePath,
            key => new Lazy<Task<CachedItem>>(() => DownloadAsync(key, entry, localPath, CancellationToken.None)));
        try
        {
            // The shared download is not cancelled by one waiter giving up.
            return await download.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (download.IsValueCreated && download.Value.IsCompleted)
                _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<CachedItem>>>(remotePath, download));
        }
    }

    /// <summary>True when the cache file exists and still has the size and write time of the downloaded version.</summary>
    private static bool MatchesVersion(string localPath, FsVersion version)
    {
        var info = new FileInfo(localPath);
        return info.Exists && info.Length == version.Size && info.LastWriteTimeUtc.ToFileTimeUtc() == version.LastWriteTime;
    }

    /// <summary>Local path a remote path is cached at.</summary>
    public string GetLocalPath(string remotePath)
        => remotePath.Length == 0 ? _cacheRoot : Path.Join(_cacheRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<CachedItem> DownloadAsync(string remotePath, FsEntry entry, string localPath, CancellationToken cancellationToken)
    {
        // Leave the GetOrAdd factory before doing I/O. The entry in _downloads is removed by EnsureAsync.
        await Task.Yield();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        for (int attempt = 0; ; attempt++)
        {
            string temp = $"{localPath}.pbdl-{Guid.NewGuid():N}.tmp";
            try
            {
                await _client.DownloadAsync(remotePath, entry, temp, cancellationToken).ConfigureAwait(false);
                ApplyMetadata(temp, entry);
                if (File.Exists(localPath))
                    File.SetAttributes(localPath, FileAttributes.Normal);
                File.Move(temp, localPath, overwrite: true);
                // A read-only share makes the cached copy read only, whatever machine B says.
                bool writable = await IsWritableAsync(cancellationToken).ConfigureAwait(false);
                File.SetAttributes(localPath, Mirrored(entry.Attributes) | (writable ? 0 : FileAttributes.ReadOnly));
                _versions[remotePath] = entry.Version;
                return new CachedItem(entry, localPath);
            }
            catch (ProtocolStatusException e) when (e.Status == NtStatus.FileVersionChanged && attempt < MaxVersionRetries)
            {
                _stats.TryRemove(remotePath, out _);
                entry = await StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
                if (entry.IsDirectory)
                    throw new ProtocolStatusException(NtStatus.FileIsADirectory, "Remote file became a directory.");
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>Forgets stat and list results of <paramref name="remotePath"/> and of its parent's listing.</summary>
    public void Invalidate(string remotePath)
    {
        _stats.TryRemove(remotePath, out _);
        _lists.TryRemove(remotePath, out _);
        _lists.TryRemove(GetParent(remotePath), out _);
    }

    /// <summary>Forgets everything cached at or below <paramref name="remotePath"/> (a directory renamed or deleted).</summary>
    private void InvalidateTree(string remotePath)
    {
        Invalidate(remotePath);
        _versions.TryRemove(remotePath, out _);
        string prefix = remotePath + "/";
        foreach (string key in _stats.Keys.Concat(_lists.Keys).Concat(_versions.Keys))
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _stats.TryRemove(key, out _);
                _lists.TryRemove(key, out _);
                _versions.TryRemove(key, out _);
            }
        }
    }

    private static string GetParent(string remotePath)
    {
        int slash = remotePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : remotePath[..slash];
    }

    /// <summary>Records machine B's new metadata of <paramref name="remotePath"/> after a change made through the Broker.</summary>
    public void Update(string remotePath, FsEntry entry)
    {
        Invalidate(remotePath);
        _stats[remotePath] = new Timed<FsEntry>(entry, Environment.TickCount64 + _ttlMilliseconds);
    }

    /// <summary>The cache copy now holds exactly <paramref name="version"/> of machine B (after a write-back).</summary>
    public void MarkContent(string remotePath, FsVersion version) => _versions[remotePath] = version;

    /// <summary>The cache copy no longer matches any version of machine B.</summary>
    public void ForgetContent(string remotePath) => _versions.TryRemove(remotePath, out _);

    /// <summary>
    /// Creates <paramref name="remotePath"/> on machine B (collision if it exists there), then clears the cache name so
    /// the hook's real NtCreateFile creates the cache copy itself, under an existing parent folder.
    /// </summary>
    public async Task<CachedItem> CreateAsync(string remotePath, bool isDirectory, uint attributes, CancellationToken cancellationToken)
    {
        FsEntry entry = await _client.CreateAsync(remotePath, isDirectory, attributes, cancellationToken).ConfigureAwait(false);
        string localPath = GetLocalPath(remotePath);
        RemoveLocal(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        _versions.TryRemove(remotePath, out _);
        Update(remotePath, entry);
        return new CachedItem(entry, localPath);
    }

    /// <summary>
    /// An existing remote file is opened to be overwritten: no download (the open truncates it), only a cache file with
    /// machine B's attributes so the real open behaves as on machine B (hidden/system/read-only checks).
    /// </summary>
    public CachedItem PrepareOverwrite(string remotePath, FsEntry entry)
    {
        string localPath = GetLocalPath(remotePath);
        if (!File.Exists(localPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            RemoveLocal(localPath);
            File.WriteAllBytes(localPath, []);
            ApplyMetadata(localPath, entry);
            File.SetAttributes(localPath, Mirrored(entry.Attributes) is var a && a != 0 ? a : FileAttributes.Normal);
            _versions.TryRemove(remotePath, out _);
        }
        return new CachedItem(entry, localPath);
    }

    public async Task<FsEntry> SetInfoAsync(FsSetInfoRequest request, CancellationToken cancellationToken)
    {
        FsEntry entry = await _client.SetInfoAsync(request, cancellationToken).ConfigureAwait(false);
        Update(request.Path, entry);
        return entry;
    }

    /// <summary>
    /// Renames on machine B, then prepares the cache for the hook, which renames its open cache handle: the new cache
    /// folder exists and a stale cache file at the new name is gone.
    /// </summary>
    public async Task<CachedItem> RenameAsync(string remotePath, string newRemotePath, bool replaceIfExists, CancellationToken cancellationToken)
    {
        FsEntry entry = await _client.RenameAsync(remotePath, newRemotePath, replaceIfExists, cancellationToken).ConfigureAwait(false);
        bool hadVersion = _versions.TryRemove(remotePath, out FsVersion version);
        InvalidateTree(remotePath);
        InvalidateTree(newRemotePath);
        string newLocalPath = GetLocalPath(newRemotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(newLocalPath)!);
        if (!GetLocalPath(remotePath).Equals(newLocalPath, StringComparison.OrdinalIgnoreCase))
            RemoveLocal(newLocalPath);
        if (hadVersion && !entry.IsDirectory)
            _versions[newRemotePath] = version;
        Update(newRemotePath, entry);
        return new CachedItem(entry, newLocalPath);
    }

    /// <summary>Deletes on machine B, then drops the cache copy.</summary>
    /// <param name="keepItem">The caller deletes the cache file or placeholder itself through its open handle; only
    /// stale cache copies below a directory are removed here.</param>
    /// <param name="expectedFileId">File id the caller saw; another file under the name is left alone (returns false).</param>
    public async Task<bool> DeleteAsync(string remotePath, bool keepItem, ulong? expectedFileId, CancellationToken cancellationToken)
    {
        bool deleted = await _client.DeleteAsync(remotePath, expectedFileId, cancellationToken).ConfigureAwait(false);
        InvalidateTree(remotePath);
        if (!deleted)
            return false;
        string localPath = GetLocalPath(remotePath);
        if (Directory.Exists(localPath))
        {
            // Machine B's directory was empty: leftovers in the placeholder are stale cache copies. The placeholder
            // itself may be held open by the caller, which deletes it.
            foreach (string child in Directory.EnumerateFileSystemEntries(localPath))
                RemoveLocal(child);
            if (!keepItem)
                TryDeleteDirectory(localPath);
        }
        else if (!keepItem)
        {
            RemoveLocal(localPath);
        }
        return true;
    }

    /// <summary>Removes a stale cache file or folder tree; in-use entries stay.</summary>
    private static void RemoveLocal(string localPath)
    {
        if (Directory.Exists(localPath))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(localPath))
                RemoveLocal(child);
            TryDeleteDirectory(localPath);
            return;
        }
        if (!File.Exists(localPath))
            return;
        try
        {
            File.SetAttributes(localPath, FileAttributes.Normal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        // Rename first: the name is free at once even when a handle still holds the file (a delete would leave it
        // delete-pending under its name until that handle closes, and every open or replace of the name would fail).
        string doomed = $"{localPath}.pbdl-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Move(localPath, doomed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            doomed = localPath;
        }
        TryDelete(doomed);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ApplyMetadata(string path, FsEntry entry)
    {
        File.SetCreationTimeUtc(path, DateTime.FromFileTimeUtc(entry.CreationTime));
        File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc(entry.LastWriteTime));
        File.SetLastAccessTimeUtc(path, DateTime.FromFileTimeUtc(entry.LastAccessTime));
    }

    private static FileAttributes Mirrored(uint attributes) => (FileAttributes)attributes & MirroredAttributes;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Timed<T>(T Value, long ExpiresAt)
    {
        public bool IsExpired => Environment.TickCount64 >= ExpiresAt;
    }
}
