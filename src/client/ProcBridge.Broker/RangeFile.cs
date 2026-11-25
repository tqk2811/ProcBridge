using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// A large file cached by ranges (M8, plan 4.4): the cache copy is a sparse file of the right size and blocks are
/// downloaded when a read needs them. The block table lives in memory next to the open write handle; once every block
/// is in, the copy gets machine B's times, the handle is closed and the copy is an ordinary complete cache file.
/// A read that continues where the previous one ended prefetches the next blocks in the background.
/// Thread safe. Owned by <see cref="FileCache"/>.
/// </summary>
internal sealed class RangeFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    private readonly AgentClient _client;
    private readonly FileStream _stream;
    private readonly int _blockSize;
    private readonly int _prefetchBlocks;
    private readonly long _blockCount;
    private readonly bool[] _present;
    private readonly ConcurrentDictionary<long, Lazy<Task>> _fetches = new();
    private readonly object _lock = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _missing;
    private long _sequentialEnd = -1;
    private long _bytesDownloaded;
    private bool _closed;

    private RangeFile(uint id, string remotePath, FsEntry entry, string localPath, FileStream stream, AgentClient client, int blockSize, int prefetchBlocks)
    {
        Id = id;
        RemotePath = remotePath;
        Entry = entry;
        LocalPath = localPath;
        _stream = stream;
        _client = client;
        _blockSize = blockSize;
        _prefetchBlocks = prefetchBlocks;
        _blockCount = (entry.Size + blockSize - 1) / blockSize;
        _present = new bool[_blockCount];
        _missing = _blockCount;
    }

    public uint Id { get; }

    public string RemotePath { get; }

    /// <summary>Machine B's metadata of the version this copy holds.</summary>
    public FsEntry Entry { get; }

    public FsVersion Version => Entry.Version;

    public string LocalPath { get; }

    public bool IsComplete => _completed.Task.IsCompleted;

    /// <summary>Completes when the last block is in (faults never: a failed block only fails its readers).</summary>
    public Task Completed => _completed.Task;

    /// <summary>Bytes downloaded into this copy so far (prefetch included).</summary>
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);

    /// <summary>Raised once when the copy became complete, before <see cref="Completed"/> completes.</summary>
    public Action<RangeFile>? OnComplete { get; set; }

    /// <summary>Creates the sparse cache file at <paramref name="localPath"/> (replacing nothing: the caller freed the name).</summary>
    public static RangeFile Create(uint id, string remotePath, FsEntry entry, string localPath, AgentClient client, int blockSize, int prefetchBlocks)
    {
        // Share everything: the hook opens the copy for the process while this handle still writes blocks into it.
        var stream = new FileStream(localPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
        try
        {
            if (!DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException($"FSCTL_SET_SPARSE failed: {Marshal.GetLastPInvokeError()}");
            stream.SetLength(entry.Size);
            var file = new RangeFile(id, remotePath, entry, localPath, stream, client, Math.Max(4096, blockSize), Math.Max(0, prefetchBlocks));
            file.ApplyTimes();
            if (file._blockCount == 0)
                file.Finish();
            return file;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Makes sure [<paramref name="offset"/>, +<paramref name="length"/>) is on disk.</summary>
    public async Task EnsureAsync(long offset, long length, CancellationToken cancellationToken)
    {
        if (IsComplete || length <= 0 || offset >= Entry.Size || offset < 0)
            return;
        long end = Math.Min(Entry.Size, offset + length);
        long first = offset / _blockSize;
        long last = (end - 1) / _blockSize;
        var waits = new List<Task>();
        for (long block = first; block <= last; block++)
        {
            if (!IsPresent(block))
                waits.Add(Fetch(block));
        }

        // Sequential read: the read starts where the previous one ended (or inside the block it ended in).
        bool sequential;
        lock (_lock)
        {
            sequential = _sequentialEnd >= 0 && offset >= _sequentialEnd - _blockSize && offset <= _sequentialEnd;
            _sequentialEnd = end;
        }
        if (sequential)
        {
            for (long block = last + 1; block <= Math.Min(_blockCount - 1, last + _prefetchBlocks); block++)
            {
                if (!IsPresent(block))
                    _ = Fetch(block).ContinueWith(static x => _ = x.Exception, TaskScheduler.Default);
            }
        }
        if (waits.Count > 0)
            await Task.WhenAll(waits).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads every missing block.</summary>
    public async Task CompleteAsync(int parallelism, CancellationToken cancellationToken)
    {
        if (IsComplete)
            return;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Missing(), parallel, async (block, token) => await Fetch(block).WaitAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        await _completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<long> Missing()
    {
        for (long block = 0; block < _blockCount; block++)
        {
            if (!IsPresent(block))
                yield return block;
        }
    }

    private bool IsPresent(long block)
    {
        lock (_lock)
            return _present[block];
    }

    /// <summary>The shared download of <paramref name="block"/>; a failed one is forgotten so the next reader tries again.</summary>
    private Task Fetch(long block)
    {
        Lazy<Task> fetch = _fetches.GetOrAdd(block, b => new Lazy<Task>(() => FetchBlockAsync(b)));
        Task task = fetch.Value;
        if (task.IsFaulted || task.IsCanceled)
            _fetches.TryRemove(new KeyValuePair<long, Lazy<Task>>(block, fetch));
        return task;
    }

    private async Task FetchBlockAsync(long block)
    {
        await Task.Yield();
        long offset = block * _blockSize;
        int length = (int)Math.Min(_blockSize, Entry.Size - offset);
        try
        {
            await _client.ReadRangeAsync(RemotePath, Entry.Version, offset, length, _stream.SafeFileHandle, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            _fetches.TryRemove(block, out _);
            throw;
        }
        Interlocked.Add(ref _bytesDownloaded, length);
        bool last;
        lock (_lock)
        {
            if (_present[block])
                return;
            _present[block] = true;
            last = --_missing == 0;
        }
        _fetches.TryRemove(block, out _);
        if (last)
            Finish();
    }

    private void Finish()
    {
        lock (_lock)
        {
            if (_closed)
                return;
            _closed = true;
            try
            {
                ApplyTimes();
            }
            catch (IOException)
            {
            }
            _stream.Dispose();
        }
        OnComplete?.Invoke(this);
        _completed.TrySetResult();
    }

    /// <summary>Gives the copy machine B's times (block writes move the last write time).</summary>
    private void ApplyTimes()
    {
        SafeFileHandle handle = _stream.SafeFileHandle;
        File.SetCreationTimeUtc(handle, DateTime.FromFileTimeUtc(Entry.CreationTime));
        File.SetLastWriteTimeUtc(handle, DateTime.FromFileTimeUtc(Entry.LastWriteTime));
        File.SetLastAccessTimeUtc(handle, DateTime.FromFileTimeUtc(Entry.LastAccessTime));
    }

    /// <summary>Closes the write handle without completing (Broker shutdown or the copy was replaced).</summary>
    public void Abandon()
    {
        lock (_lock)
        {
            if (_closed)
                return;
            _closed = true;
            _stream.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
