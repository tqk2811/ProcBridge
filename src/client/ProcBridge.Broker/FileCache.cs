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

    public FileCache(AgentClient client, string cacheRoot, TimeSpan metadataTtl)
    {
        _client = client;
        _cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        _ttlMilliseconds = (long)metadataTtl.TotalMilliseconds;
        Directory.CreateDirectory(_cacheRoot);
    }

    public string CacheRoot => _cacheRoot;

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

        if (_versions.TryGetValue(remotePath, out FsVersion version) && version == entry.Version && File.Exists(localPath))
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
                File.SetAttributes(localPath, MirroredOrNormal(entry.Attributes));
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

    private static void ApplyMetadata(string path, FsEntry entry)
    {
        File.SetCreationTimeUtc(path, DateTime.FromFileTimeUtc(entry.CreationTime));
        File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc(entry.LastWriteTime));
        File.SetLastAccessTimeUtc(path, DateTime.FromFileTimeUtc(entry.LastAccessTime));
    }

    private static FileAttributes MirroredOrNormal(uint attributes)
    {
        var mirrored = (FileAttributes)attributes & MirroredAttributes;
        return mirrored == 0 ? FileAttributes.Normal : mirrored;
    }

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
