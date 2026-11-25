using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Broker;

/// <summary>
/// Typed client for one share on one Agent. Owns its <see cref="ConnectionPool"/>; large files are downloaded as
/// chunks fetched in parallel over the pool's connections.
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    private readonly TransportOptions _options;

    public AgentClient(IConnectionFactory factory, string shareName, TransportOptions options, string clientName = "ProcBridge.Broker")
    {
        _options = options;
        var hello = new HelloRequest { ProtocolVersion = ProtocolInfo.Version, ClientName = clientName, ShareName = shareName };
        Pool = new ConnectionPool(factory, hello, options);
    }

    /// <summary>Connection pool, exposed for diagnostics (request spread, reconnect count).</summary>
    public ConnectionPool Pool { get; }

    public async Task<FsEntry> StatAsync(string remotePath, CancellationToken cancellationToken)
    {
        Frame frame = await Pool.SendAsync(MessageType.FsStat, ProtocolSerializer.Serialize(new FsStatRequest { Path = remotePath }), default, idempotent: true, cancellationToken).ConfigureAwait(false);
        return ProtocolSerializer.Deserialize<FsStatResponse>(frame.Header).Entry;
    }

    /// <summary>Lists a whole directory, following the server's pages.</summary>
    public async Task<IReadOnlyList<FsEntry>> ListAsync(string remotePath, CancellationToken cancellationToken)
    {
        var entries = new List<FsEntry>();
        while (true)
        {
            var request = new FsListRequest { Path = remotePath, StartIndex = entries.Count };
            Frame frame = await Pool.SendAsync(MessageType.FsList, ProtocolSerializer.Serialize(request), default, idempotent: true, cancellationToken).ConfigureAwait(false);
            FsListResponse page = ProtocolSerializer.Deserialize<FsListResponse>(frame.Header);
            entries.AddRange(page.Entries);
            if (!page.HasMore || page.Entries.Length == 0)
                return entries;
        }
    }

    /// <summary>
    /// Downloads the whole file into a new file at <paramref name="destinationPath"/>. Every chunk is checked
    /// against <paramref name="entry"/>'s version; a change on machine B mid-download fails with
    /// <see cref="NtStatus.FileVersionChanged"/> and the caller retries with fresh metadata.
    /// </summary>
    public async Task DownloadAsync(string remotePath, FsEntry entry, string destinationPath, CancellationToken cancellationToken)
    {
        int maxRead = await Pool.GetMaxReadSizeAsync(cancellationToken).ConfigureAwait(false);
        int chunkSize = Math.Max(1, Math.Min(_options.ReadChunkSize, maxRead));
        long size = entry.Size;
        FsVersion expected = entry.Version;

        using var handle = File.OpenHandle(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, preallocationSize: size);
        long chunkCount = (size + chunkSize - 1) / chunkSize;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelChunks, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(LongRange(chunkCount), parallel, async (index, token) =>
        {
            long offset = index * chunkSize;
            int length = (int)Math.Min(chunkSize, size - offset);
            var request = new FsReadRequest { Path = remotePath, Offset = offset, Length = length, ExpectedVersion = expected };
            Frame frame = await Pool.SendAsync(MessageType.FsRead, ProtocolSerializer.Serialize(request), default, idempotent: true, token).ConfigureAwait(false);
            if (frame.Payload.Length != length)
                throw new ProtocolStatusException(NtStatus.FileVersionChanged, $"Short read at {offset}: {frame.Payload.Length} of {length} bytes.");
            await RandomAccess.WriteAsync(handle, frame.Payload, offset, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>True when the share accepts writes (from the Hello of a pooled connection).</summary>
    public async Task<bool> IsWritableAsync(CancellationToken cancellationToken)
    {
        HelloResponse hello = await Pool.GetServerHelloAsync(cancellationToken).ConfigureAwait(false);
        return hello.Writable;
    }

    public Task<FsEntry> CreateAsync(string remotePath, bool isDirectory, uint attributes, CancellationToken cancellationToken)
        => SendStatAsync(MessageType.FsCreate, ProtocolSerializer.Serialize(new FsCreateRequest { Path = remotePath, IsDirectory = isDirectory, Attributes = attributes }), cancellationToken);

    public Task<FsEntry> SetInfoAsync(FsSetInfoRequest request, CancellationToken cancellationToken)
        => SendStatAsync(MessageType.FsSetInfo, ProtocolSerializer.Serialize(request), cancellationToken);

    public Task<FsEntry> RenameAsync(string remotePath, string newRemotePath, bool replaceIfExists, CancellationToken cancellationToken)
        => SendStatAsync(MessageType.FsRename, ProtocolSerializer.Serialize(new FsRenameRequest { Path = remotePath, NewPath = newRemotePath, ReplaceIfExists = replaceIfExists }), cancellationToken);

    /// <summary>Deletes on machine B; false when <paramref name="expectedFileId"/> was given and another file has the name.</summary>
    public async Task<bool> DeleteAsync(string remotePath, ulong? expectedFileId, CancellationToken cancellationToken)
    {
        var request = new FsDeleteRequest { Path = remotePath, ExpectedFileId = expectedFileId };
        Frame frame = await Pool.SendAsync(MessageType.FsDelete, ProtocolSerializer.Serialize(request), default, idempotent: false, cancellationToken).ConfigureAwait(false);
        return ProtocolSerializer.Deserialize<FsDeleteResponse>(frame.Header).Deleted;
    }

    private async Task<FsEntry> SendStatAsync(MessageType type, byte[] header, CancellationToken cancellationToken)
    {
        Frame frame = await Pool.SendAsync(type, header, default, idempotent: false, cancellationToken).ConfigureAwait(false);
        return ProtocolSerializer.Deserialize<FsStatResponse>(frame.Header).Entry;
    }

    /// <summary>
    /// Uploads <paramref name="sourcePath"/> as the new content of <paramref name="remotePath"/>: blocks in parallel into
    /// machine B's staging file, then one commit that checks <paramref name="expectedVersion"/> (plan section 10).
    /// </summary>
    /// <param name="uploadId">32 lowercase hex digits, the same for every attempt of one write-back: machine B answers a
    /// commit it already applied instead of applying it again.</param>
    public async Task<FsCommitResponse> UploadAsync(string remotePath, string sourcePath, string uploadId, FsVersion? expectedVersion, ConflictPolicy policy, CancellationToken cancellationToken)
    {
        int maxRead = await Pool.GetMaxReadSizeAsync(cancellationToken).ConfigureAwait(false);
        int chunkSize = Math.Max(1, Math.Min(_options.ReadChunkSize, maxRead));
        long size;
        long lastWriteTime;
        using (var handle = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous))
        {
            size = RandomAccess.GetLength(handle);
            lastWriteTime = File.GetLastWriteTimeUtc(handle).ToFileTimeUtc();
            long chunkCount = (size + chunkSize - 1) / chunkSize;
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelChunks, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(LongRange(chunkCount), parallel, async (index, token) =>
            {
                long offset = index * chunkSize;
                byte[] buffer = new byte[(int)Math.Min(chunkSize, size - offset)];
                int read = await RandomAccess.ReadAsync(handle, buffer, offset, token).ConfigureAwait(false);
                var request = new FsWriteRequest { Path = remotePath, UploadId = uploadId, Offset = offset };
                // Writing the same block twice gives the same staging file, so a resend after a lost connection is safe.
                await Pool.SendAsync(MessageType.FsWrite, ProtocolSerializer.Serialize(request), buffer.AsMemory(0, read), idempotent: true, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        var commit = new FsCommitRequest { Path = remotePath, UploadId = uploadId, Size = size, ExpectedVersion = expectedVersion, LastWriteTime = lastWriteTime, Policy = policy };
        Frame frame = await Pool.SendAsync(MessageType.FsCommit, ProtocolSerializer.Serialize(commit), default, idempotent: false, cancellationToken).ConfigureAwait(false);
        return ProtocolSerializer.Deserialize<FsCommitResponse>(frame.Header);
    }

    private static IEnumerable<long> LongRange(long count)
    {
        for (long i = 0; i < count; i++)
            yield return i;
    }

    public ValueTask DisposeAsync() => Pool.DisposeAsync();
}
