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

    private static IEnumerable<long> LongRange(long count)
    {
        for (long i = 0; i < count; i++)
            yield return i;
    }

    public ValueTask DisposeAsync() => Pool.DisposeAsync();
}
