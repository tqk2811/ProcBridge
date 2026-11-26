using ProcBridge.Broker.Enums;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Helpers;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Answers one Hook IPC request: maps (mapping index, relative path) to a share path and asks the
/// <see cref="FileCache"/>. Every failure becomes the NTSTATUS the hooked call should return.
/// On a writable share (M3) a missing name is created on machine B before the hook creates the cache copy, opens that
/// may change content get a write token for the <see cref="WriteBackQueue"/>, and rename, delete and attribute changes
/// go to machine B synchronously (plan 4.5).
/// </summary>
public sealed class HookRequestHandler : IDisposable
{
    private const uint WriteAccessMask =
        0x0002 /* FILE_WRITE_DATA */ | 0x0004 /* FILE_APPEND_DATA */ | 0x0010 /* FILE_WRITE_EA */ |
        0x0040 /* FILE_DELETE_CHILD */ | 0x0100 /* FILE_WRITE_ATTRIBUTES */ | 0x00010000 /* DELETE */ |
        0x00040000 /* WRITE_DAC */ | 0x00080000 /* WRITE_OWNER */ | 0x02000000 /* MAXIMUM_ALLOWED */ |
        0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;
    /// <summary>Access that can change the content of a file: such opens get a write token.</summary>
    private const uint ContentWriteMask =
        0x0002 /* FILE_WRITE_DATA */ | 0x0004 /* FILE_APPEND_DATA */ | 0x02000000 /* MAXIMUM_ALLOWED */ |
        0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;
    private const uint FileSupersede = 0;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileOverwrite = 4;
    private const uint FileOverwriteIf = 5;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileDeleteOnClose = 0x00001000;
    private const long AllocationUnit = 4096;
    private const uint ShareAll = 0x7; /* FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE */

    private readonly FileCache _cache;
    private readonly IReadOnlyList<VirtualMapping> _mappings;
    private readonly WriteBackQueue? _writeBack;
    private readonly bool _writeThrough;
    private readonly RemoteHolds? _holds;
    private readonly ProcessWatch? _processes;
    private readonly Lock _clientsLock = new();
    private readonly Dictionary<long, ClientState> _clients = [];
    /// <summary>Releases started by <see cref="ClientDisconnected"/> and not finished yet (under the clients lock).</summary>
    private readonly List<Task> _releases = [];

    /// <param name="writeBack">Write-back queue; null serves the share read only whatever the Agent allows (tests).</param>
    /// <param name="writeThrough">Tell hooks to flush after every write (<see cref="Models.BrokerOptions.WriteThrough"/>).</param>
    /// <param name="holds">Handles machine B keeps for writers (M8 locks and share mode); null keeps locks on machine A only.</param>
    /// <param name="processes">
    /// Watches processes holding write tokens, so what they hold is released when they exit; null releases it with the
    /// process's last pipe connection instead.
    /// </param>
    public HookRequestHandler(FileCache cache, IReadOnlyList<VirtualMapping> mappings, WriteBackQueue? writeBack = null, bool writeThrough = false,
        RemoteHolds? holds = null, ProcessWatch? processes = null)
    {
        _holds = holds;
        _processes = processes;
        if (mappings.Count > ushort.MaxValue)
            throw new ArgumentException("Too many mappings.", nameof(mappings));
        _cache = cache;
        _mappings = mappings;
        Watches = new HookWatchRegistry(cache);
        _writeBack = writeBack;
        _writeThrough = writeThrough;
    }

    /// <summary>A pipe connection of client <paramref name="clientId"/> (its process id) opened.</summary>
    public void ClientConnected(long clientId)
    {
        Watches.Attach(clientId);
        lock (_clientsLock)
        {
            if (!_clients.TryGetValue(clientId, out ClientState? client))
            {
                ProcessKey key = ResolveKey(clientId);
                // Shared a token (Share) before it connected: already watched.
                client = new ClientState(key) { Watched = _processes?.IsWatching(key) == true };
                _clients.Add(clientId, client);
            }
            client.Connections++;
        }
    }

    /// <summary>
    /// A pipe connection of <paramref name="clientId"/> closed; the watches of its last connection go with it. A process
    /// that is not watched (it cannot be opened) is taken as gone with its last connection: the write tokens it still
    /// holds are released, like the system releases the locks of a dead process.
    /// </summary>
    public void ClientDisconnected(long clientId)
    {
        Watches.Detach(clientId);
        ProcessKey? released = null;
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(clientId, out ClientState? client) && --client.Connections <= 0)
            {
                _clients.Remove(clientId);
                if (!client.Watched)
                    released = client.Key;
            }
        }
        if (released is { } key && _writeBack is not null)
        {
            Task release = _writeBack.ReleaseProcessAsync(key, CancellationToken.None);
            lock (_clientsLock)
            {
                _releases.RemoveAll(x => x.IsCompleted);
                if (!release.IsCompleted)
                    _releases.Add(release);
            }
        }
    }

    /// <summary>
    /// Waits for the write-backs that the last connections of unwatched processes started; the Broker awaits it after
    /// the pipe closed, before the write-back queue goes.
    /// </summary>
    public Task WaitReleasesAsync()
    {
        lock (_clientsLock)
            return Task.WhenAll(_releases.ToArray());
    }

    /// <summary>
    /// Looks up a process by id: its key as it runs now, or null with whether that was for lack of rights (the process may
    /// still run) rather than because it is gone. Tests replace it to play a process the Broker cannot open.
    /// </summary>
    internal Func<int, (ProcessKey? Key, bool AccessDenied)> ProcessLookup { get; set; } = processId =>
    {
        ProcessKey? key = ProcessWatch.TryGetKey(processId, out bool accessDenied);
        return (key, accessDenied);
    };

    /// <summary>The process behind <paramref name="clientId"/> as a write token holder.</summary>
    private ProcessKey GetHolder(long clientId)
    {
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(clientId, out ClientState? client))
                return client.Key;
        }
        return ResolveKey(clientId);
    }

    /// <summary>
    /// The process of a pipe client with its creation time; a client the Broker cannot open (or without a process id) gets
    /// a key of its own without creation time, released with its last connection.
    /// </summary>
    private ProcessKey ResolveKey(long clientId)
    {
        if (clientId is > 0 and <= int.MaxValue && ProcessLookup((int)clientId).Key is { } key)
            return key;
        return new ProcessKey((int)Math.Clamp(clientId, int.MinValue, int.MaxValue), 0);
    }

    /// <summary>
    /// The holder key of a process named by another one (Share target: pid and creation time from the hook), the same key
    /// its own pipe connection gets from <see cref="ResolveKey"/>: a process the Broker cannot open becomes (pid, 0), so its
    /// Close and Adopt find the holder and its last connection releases it. A process that is gone (or whose id now
    /// belongs to another process) keeps the given key, so the watch reports it gone at once.
    /// </summary>
    private ProcessKey ToHolderKey(ProcessKey target)
    {
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(target.ProcessId, out ClientState? client) && (client.Key == target || client.Key.CreationTime == 0))
                return client.Key;
        }
        (ProcessKey? now, bool accessDenied) = ProcessLookup(target.ProcessId);
        return now is null && accessDenied ? new ProcessKey(target.ProcessId, 0) : target;
    }

    /// <summary>
    /// <paramref name="holder"/> now holds a write token: watch it so the token is released when it exits. Call after
    /// the holder was added, so an exit reported at once finds it.
    /// </summary>
    private void WatchHolder(ProcessKey holder)
    {
        if (_processes is null || _processes.Watch(holder) == ProcessWatchResult.Unavailable)
            return;
        lock (_clientsLock)
        {
            foreach (ClientState client in _clients.Values)
            {
                if (client.Key == holder)
                    client.Watched = true;
            }
        }
    }

    /// <summary>Directory watches of the hook (WatchDirectory, WaitChanges, WaitAnyChanges, Unwatch).</summary>
    public HookWatchRegistry Watches { get; }

    public void Dispose() => Watches.Dispose();

    /// <summary>
    /// Handles <paramref name="request"/> of client <paramref name="clientId"/> and writes the reply into
    /// <paramref name="reply"/>; returns its size.
    /// </summary>
    public async Task<int> HandleAsync(long clientId, ReadOnlyMemory<byte> request, Memory<byte> reply, CancellationToken cancellationToken)
    {
        HookIpcHeader header;
        try
        {
            header = HookIpcCodec.ReadHeader(request.Span);
        }
        catch (HookIpcFormatException)
        {
            return HookIpcCodec.EncodeError(reply.Span, 0, 0, (uint)NtStatus.InvalidParameter);
        }

        try
        {
            IHookIpcMessage response = await DispatchAsync(clientId, header.MessageType, request, cancellationToken).ConfigureAwait(false);
            return HookIpcCodec.Encode(reply.Span, header.RequestId, response);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            NtStatus status = e is HookIpcFormatException ? NtStatus.InvalidParameter : ToHookStatus(NtStatusMapper.FromException(e));
            return HookIpcCodec.EncodeError(reply.Span, header.MessageType, header.RequestId, (uint)status);
        }
    }

    /// <summary>
    /// The hooked process only understands standard NTSTATUS values: ProcBridge's own codes (0xE000000x, customer
    /// bit set) are replaced by the closest standard one before they cross the IPC boundary.
    /// </summary>
    public static NtStatus ToHookStatus(NtStatus status) => status switch
    {
        NtStatus.ProtocolError => NtStatus.InvalidNetworkResponse,
        NtStatus.FileVersionChanged or NtStatus.FrameTooLarge => NtStatus.UnexpectedNetworkError,
        _ => ((uint)status & 0x20000000u) != 0 ? NtStatus.UnexpectedNetworkError : status,
    };

    private async Task<IHookIpcMessage> DispatchAsync(long clientId, HookIpcMessageType type, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case HookIpcMessageType.Hello:
            {
                HookHelloRequest hello = Read(request, HookHelloRequest.Read);
                ProcessKey seen = GetHolder(clientId);
                if (seen.CreationTime != 0 && (long)hello.ProcessCreationTime != seen.CreationTime)
                    System.Diagnostics.Trace.TraceWarning($"ProcBridge: hook of process {hello.ProcessId} claims another creation time than pipe client {clientId}.");
                return new HookHelloReply
                {
                    VirtualRoots = _mappings.Select(x => x.VirtualRoot).ToArray(),
                    Flags = _writeThrough ? HookIpcProtocol.HelloFlagWriteThrough : (ushort)0,
                };
            }

            case HookIpcMessageType.Stat:
            {
                HookStatRequest stat = Read(request, HookStatRequest.Read);
                FsEntry entry = await _cache.StatAsync(ToRemotePath(stat.MappingIndex, stat.RelativePath), cancellationToken).ConfigureAwait(false);
                return new HookStatReply { Info = ToFileInfo(entry) };
            }

            case HookIpcMessageType.Resolve:
            {
                HookResolveRequest resolve = Read(request, HookResolveRequest.Read);
                string remotePath = ToRemotePath(resolve.MappingIndex, resolve.RelativePath);
                if (_writeBack is not null && await _cache.IsWritableAsync(cancellationToken).ConfigureAwait(false))
                    return await ResolveWritableAsync(clientId, resolve, remotePath, cancellationToken).ConfigureAwait(false);
                bool mayCreate = resolve.CreateDisposition is FileCreate or FileOpenIf or FileOverwriteIf;
                if ((resolve.DesiredAccess & WriteAccessMask) != 0
                    || (resolve.CreateOptions & FileDeleteOnClose) != 0
                    || resolve.CreateDisposition is not (FileOpen or FileOpenIf))
                {
                    // Report a missing file as such before the write-protect error, like a read-only volume does;
                    // when the call would create it, the volume's answer is the write-protect error itself.
                    // FILE_CREATE on an existing name fails with a collision first, as on a local volume.
                    try
                    {
                        await _cache.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ProtocolStatusException e) when (mayCreate && e.Status == NtStatus.ObjectNameNotFound)
                    {
                        throw new ProtocolStatusException(NtStatus.MediaWriteProtected, "Share is read only.");
                    }
                    if (resolve.CreateDisposition == FileCreate)
                        throw new ProtocolStatusException(NtStatus.ObjectNameCollision, "Name already exists.");
                    throw new ProtocolStatusException(NtStatus.MediaWriteProtected, "Share is read only.");
                }

                try
                {
                    CachedItem item = await _cache.EnsureAsync(remotePath, allowRanges: true, cancellationToken).ConfigureAwait(false);
                    return new HookResolveReply { Info = ToFileInfo(item.Entry) with { RangeId = item.RangeId }, CachePath = item.LocalPath };
                }
                catch (ProtocolStatusException e) when (resolve.CreateDisposition == FileOpenIf && e.Status == NtStatus.ObjectNameNotFound)
                {
                    // FILE_OPEN_IF would create the missing file.
                    throw new ProtocolStatusException(NtStatus.MediaWriteProtected, "Share is read only.");
                }
            }

            case HookIpcMessageType.FetchRange:
            {
                HookFetchRangeRequest fetch = Read(request, HookFetchRangeRequest.Read);
                bool complete = await _cache.FetchRangeAsync(fetch.RangeId, fetch.Offset, fetch.Length, fetch.Whole, cancellationToken).ConfigureAwait(false);
                return new HookFetchRangeReply { Flags = complete ? HookIpcProtocol.FetchReplyFlagComplete : 0 };
            }

            case HookIpcMessageType.Lock:
            case HookIpcMessageType.Unlock:
            {
                HookLockRequest range = Read(request, (ref HookIpcReader r) => HookLockRequest.Read(ref r, type));
                if (range.Offset < 0 || range.Length < 0)
                    throw new ProtocolStatusException(NtStatus.InvalidParameter, "Negative lock range.");
                if (_holds is not null)
                {
                    RequireHolder(range.WriteToken, clientId);
                    if (type == HookIpcMessageType.Lock)
                        await _holds.LockAsync(range.WriteToken, range.Offset, range.Length, range.Exclusive, range.FailImmediately, cancellationToken).ConfigureAwait(false);
                    else
                        await _holds.UnlockAsync(range.WriteToken, range.Offset, range.Length, cancellationToken).ConfigureAwait(false);
                }
                return new HookEmptyReply { Type = type };
            }

            case HookIpcMessageType.List:
            {
                HookListRequest list = Read(request, HookListRequest.Read);
                IReadOnlyList<FsEntry> entries = await _cache.ListAsync(ToRemotePath(list.MappingIndex, list.RelativePath), cancellationToken).ConfigureAwait(false);
                return BuildListPage(entries, (int)Math.Min(list.StartIndex, int.MaxValue));
            }

            case HookIpcMessageType.WatchDirectory:
            {
                HookWatchRequest watch = Read(request, HookWatchRequest.Read);
                string remotePath = ToRemotePath(watch.MappingIndex, watch.RelativePath);
                FsEntry entry = await _cache.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
                if (!entry.IsDirectory)
                    throw new ProtocolStatusException(NtStatus.NotADirectory, "Only directories can be watched.");
                return new HookWatchReply { WatchId = Watches.Add(clientId, remotePath, watch.WatchSubtree) };
            }

            case HookIpcMessageType.WaitChanges:
            {
                HookWaitChangesRequest wait = Read(request, HookWaitChangesRequest.Read);
                TimeSpan timeout = wait.TimeoutMilliseconds == HookIpcProtocol.InfiniteTimeout
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(wait.TimeoutMilliseconds);
                return await Watches.WaitAsync(clientId, wait.WatchId, timeout, cancellationToken).ConfigureAwait(false);
            }

            case HookIpcMessageType.WaitAnyChanges:
            {
                HookWaitAnyChangesRequest wait = Read(request, HookWaitAnyChangesRequest.Read);
                TimeSpan timeout = wait.TimeoutMilliseconds == HookIpcProtocol.InfiniteTimeout
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(wait.TimeoutMilliseconds);
                return await Watches.WaitAnyAsync(clientId, timeout, cancellationToken).ConfigureAwait(false);
            }

            case HookIpcMessageType.Unwatch:
                Watches.Remove(clientId, Read(request, HookUnwatchRequest.Read).WatchId);
                return new HookUnwatchReply();
            case HookIpcMessageType.Close:
            {
                HookTokenRequest close = Read(request, (ref HookIpcReader r) => HookTokenRequest.Read(ref r, type));
                // The hold on machine B goes when the token ends (WriteBackQueue.TokenEnded), after the last write-back:
                // a commit writes through the held handle while it is open.
                if (_writeBack is not null)
                    await _writeBack.CloseAsync(close.WriteToken, GetHolder(clientId), cancellationToken).ConfigureAwait(false);
                return new HookEmptyReply { Type = type };
            }

            case HookIpcMessageType.Share:
            {
                HookShareRequest share = Read(request, HookShareRequest.Read);
                ProcessKey sender = GetHolder(clientId);
                if (_writeBack is null || share.TargetProcessId == 0 || share.TargetProcessId > int.MaxValue)
                    return new HookEmptyReply { Type = type };
                ProcessKey target = ToHolderKey(new ProcessKey((int)share.TargetProcessId, (long)share.TargetCreationTime));
                // Only a holder may share a token: anything else could keep a file open for another process.
                if (!_writeBack.IsHolder(share.WriteToken, sender))
                {
                    System.Diagnostics.Trace.TraceWarning($"ProcBridge: process {sender.ProcessId} shared write token {share.WriteToken} it does not hold; ignored.");
                    return new HookEmptyReply { Type = type };
                }
                if (_writeBack.AddHolder(share.WriteToken, target))
                    WatchHolder(target);
                return new HookEmptyReply { Type = type };
            }

            case HookIpcMessageType.Adopt:
            {
                HookAdoptRequest adopt = Read(request, HookAdoptRequest.Read);
                string remotePath = ToRemotePath(adopt.MappingIndex, adopt.RelativePath);
                FsEntry entry = await _cache.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
                HookFileInfo info = ToFileInfo(entry) with { RangeId = _cache.GetRangeId(remotePath) };
                if (_writeBack is null || entry.IsDirectory)
                    return new HookAdoptReply { Info = info };
                ProcessKey holder = GetHolder(clientId);
                bool write = (adopt.Flags & HookIpcProtocol.AdoptFlagWrite) != 0;
                ulong token = _writeBack.Adopt(remotePath, holder, write, entry.Version, out bool created);
                if (token == 0)
                    return new HookAdoptReply { Info = info };
                if (created && _holds is not null)
                {
                    // A writer nobody holds any more (its opener closed or died): machine B holds the file for it with
                    // the widest share mode, its own is not known.
                    try
                    {
                        await _holds.OpenAsync(token, remotePath, ShareAll, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        System.Diagnostics.Trace.TraceWarning($"ProcBridge: cannot hold '{remotePath}' on machine B for an adopted handle: {e.Message}");
                    }
                }
                WatchHolder(holder);
                return new HookAdoptReply { Info = info, WriteToken = token };
            }

            case HookIpcMessageType.Flush:
            {
                HookTokenRequest flush = Read(request, (ref HookIpcReader r) => HookTokenRequest.Read(ref r, type));
                if (_writeBack is not null)
                {
                    RequireHolder(flush.WriteToken, clientId);
                    await _writeBack.FlushAsync(flush.WriteToken, cancellationToken).ConfigureAwait(false);
                }
                return new HookEmptyReply { Type = type };
            }

            case HookIpcMessageType.SetInfo:
            {
                HookSetInfoRequest set = Read(request, HookSetInfoRequest.Read);
                string remotePath = ToRemotePath(set.MappingIndex, set.RelativePath);
                await RequireWritableAsync(cancellationToken).ConfigureAwait(false);
                // While a writer has the file open, its size and last write time travel with the write-back of the cache
                // copy (which already holds them). Setting them on machine B now would give machine B's old content the
                // cache copy's version, and the write-back would then skip a rewrite of the same size.
                bool contentFieldsLater = _writeBack!.IsBusy(remotePath);
                var change = new FsSetInfoRequest
                {
                    Path = remotePath,
                    Attributes = set.Fields.HasFlag(HookSetInfoFields.Attributes) ? set.Attributes : null,
                    CreationTime = set.Fields.HasFlag(HookSetInfoFields.CreationTime) ? set.CreationTime : null,
                    LastAccessTime = set.Fields.HasFlag(HookSetInfoFields.LastAccessTime) ? set.LastAccessTime : null,
                    LastWriteTime = set.Fields.HasFlag(HookSetInfoFields.LastWriteTime) && !contentFieldsLater ? set.LastWriteTime : null,
                    EndOfFile = set.Fields.HasFlag(HookSetInfoFields.EndOfFile) && !contentFieldsLater ? set.EndOfFile : null,
                };
                if (change.Attributes is null && change.CreationTime is null && change.LastAccessTime is null
                    && change.LastWriteTime is null && change.EndOfFile is null)
                    return new HookStatReply { Info = ToFileInfo(await _cache.StatAsync(remotePath, cancellationToken).ConfigureAwait(false)), Type = type };
                FsEntry entry;
                // Not in the middle of a write-back of the same file: machine B's commit keeps the attributes it read
                // before replacing the file, so a change landing between that read and the replace would be undone.
                using (await _writeBack!.LockPathsAsync([remotePath], cancellationToken).ConfigureAwait(false))
                    entry = await _cache.SetInfoAsync(change, cancellationToken).ConfigureAwait(false);
                return new HookStatReply { Info = ToFileInfo(entry), Type = type };
            }

            case HookIpcMessageType.Rename:
            {
                HookRenameRequest rename = Read(request, HookRenameRequest.Read);
                string remotePath = ToRemotePath(rename.MappingIndex, rename.RelativePath);
                string newRemotePath = ToRemotePath(rename.NewMappingIndex, rename.NewRelativePath);
                await RequireWritableAsync(cancellationToken).ConfigureAwait(false);
                if (remotePath.Length == 0 || newRemotePath.Length == 0)
                    throw new ProtocolStatusException(NtStatus.AccessDenied, "The share root cannot be renamed.");
                bool replace = (rename.Flags & HookRenameRequest.FlagReplaceIfExists) != 0;
                CachedItem item;
                using (await _writeBack!.LockPathsAsync([remotePath, newRemotePath], cancellationToken).ConfigureAwait(false))
                {
                    item = await _cache.RenameAsync(remotePath, newRemotePath, replace, cancellationToken).ConfigureAwait(false);
                    _writeBack.Renamed(remotePath, newRemotePath);
                }
                return new HookRenameReply { Info = ToFileInfo(item.Entry), NewCachePath = item.LocalPath };
            }

            case HookIpcMessageType.Delete:
            {
                HookDeleteRequest delete = Read(request, HookDeleteRequest.Read);
                string remotePath = ToRemotePath(delete.MappingIndex, delete.RelativePath);
                await RequireWritableAsync(cancellationToken).ConfigureAwait(false);
                ulong? expectedFileId = (delete.Flags & HookDeleteRequest.FlagExpectFileId) != 0 ? delete.ExpectedFileId : null;
                using (await _writeBack!.LockPathsAsync([remotePath], cancellationToken).ConfigureAwait(false))
                {
                    // Open writers stop writing back only once machine B really deleted the file.
                    if (await _cache.DeleteAsync(remotePath, (delete.Flags & HookDeleteRequest.FlagKeepItem) != 0, expectedFileId, cancellationToken).ConfigureAwait(false))
                        _writeBack.Deleted(remotePath);
                }
                return new HookEmptyReply { Type = type };
            }

            default:
                throw new ProtocolStatusException(NtStatus.NotImplemented, $"IPC message {type} is not supported.");
        }
    }

    private async Task RequireWritableAsync(CancellationToken cancellationToken)
    {
        if (_writeBack is null || !await _cache.IsWritableAsync(cancellationToken).ConfigureAwait(false))
            throw new ProtocolStatusException(NtStatus.MediaWriteProtected, "Share is read only.");
    }

    /// <summary>
    /// NtCreateFile semantics on a writable share. Missing names are created on machine B first (collision and missing
    /// folder errors come from there); the cache name is then left free for the hook's real call to create. Existing
    /// files opened to be overwritten are not downloaded. Opens that may change content get a write token.
    /// </summary>
    private async Task<IHookIpcMessage> ResolveWritableAsync(long clientId, HookResolveRequest resolve, string remotePath, CancellationToken cancellationToken)
    {
        bool mayChange = (resolve.DesiredAccess & WriteAccessMask) != 0 || (resolve.CreateOptions & FileDeleteOnClose) != 0
            || resolve.CreateDisposition is not (FileOpen or FileOpenIf);
        if (mayChange)
            _cache.Invalidate(remotePath); // decide on machine B's current state, not a cached stat

        FsEntry? entry;
        try
        {
            entry = await _cache.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolStatusException e) when (e.Status == NtStatus.ObjectNameNotFound)
        {
            entry = null;
        }

        CachedItem item;
        bool created = false;
        if (entry is null)
        {
            if (resolve.CreateDisposition is FileOpen or FileOverwrite)
                throw new ProtocolStatusException(NtStatus.ObjectNameNotFound, "Not found.");
            if (resolve.CreateDisposition > FileOverwriteIf)
                throw new ProtocolStatusException(NtStatus.InvalidParameter, "Unknown disposition.");
            bool isDirectory = (resolve.CreateOptions & FileDirectoryFile) != 0;
            try
            {
                item = await _cache.CreateAsync(remotePath, isDirectory, resolve.FileAttributes, cancellationToken).ConfigureAwait(false);
                created = true;
            }
            catch (ProtocolStatusException e) when (e.Status == NtStatus.ObjectNameCollision && resolve.CreateDisposition != FileCreate)
            {
                // Created on machine B since the stat: open it instead.
                return await ResolveWritableAsync(clientId, resolve, remotePath, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (resolve.CreateDisposition == FileCreate)
        {
            throw new ProtocolStatusException(NtStatus.ObjectNameCollision, "Name already exists.");
        }
        else if (!entry.IsDirectory && resolve.CreateDisposition is FileSupersede or FileOverwrite or FileOverwriteIf)
        {
            item = _writeBack!.IsBusy(remotePath) ? new CachedItem(entry, _cache.GetLocalPath(remotePath)) : _cache.PrepareOverwrite(remotePath, entry);
        }
        else if (!entry.IsDirectory && _writeBack!.IsBusy(remotePath))
        {
            // Another writer has the cache copy open with changes machine B does not have yet: keep them.
            item = new CachedItem(entry, _cache.GetLocalPath(remotePath));
        }
        else
        {
            // Opens that only read may get a ranged copy; anything that may write brings the whole file in first.
            item = await _cache.EnsureAsync(remotePath, entry, allowRanges: !mayChange, cancellationToken).ConfigureAwait(false);
        }

        ulong token = 0;
        bool contentWrite = (resolve.DesiredAccess & ContentWriteMask) != 0 || resolve.CreateDisposition is FileSupersede or FileOverwrite or FileOverwriteIf
            || (created && resolve.CreateDisposition != FileOpen);
        if (contentWrite && !item.Entry.IsDirectory)
        {
            ProcessKey holder = GetHolder(clientId);
            token = _writeBack!.Open(remotePath, item.Entry.Version, holder);
            if (_holds is not null)
            {
                try
                {
                    await _holds.OpenAsync(token, remotePath, resolve.ShareAccess, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A user of machine B holds the file in a way this open forbids (or the Agent is gone): no open.
                    await _writeBack.CloseAsync(token, holder, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            WatchHolder(holder);
        }
        return new HookResolveReply { Info = ToFileInfo(item.Entry) with { RangeId = item.RangeId }, CachePath = item.LocalPath, WriteToken = token };
    }

    /// <summary>
    /// Only a process holding <paramref name="token"/> may flush or lock through it: tokens are numbered in order, so
    /// another process of the session could guess one.
    /// </summary>
    private void RequireHolder(ulong token, long clientId)
    {
        if (_writeBack is not null && !_writeBack.IsHolder(token, GetHolder(clientId)))
            throw new ProtocolStatusException(NtStatus.InvalidHandle, "The process does not hold this write token.");
    }

    private static HookListReply BuildListPage(IReadOnlyList<FsEntry> entries, int start)
    {
        int budget = HookIpcProtocol.MaxMessageSize - HookIpcProtocol.HeaderSize - HookListReply.FixedSize;
        var page = new List<HookListEntry>();
        int index = Math.Min(start, entries.Count);
        for (; index < entries.Count && page.Count < ushort.MaxValue; index++)
        {
            var item = new HookListEntry(ToFileInfo(entries[index]), entries[index].Name);
            if (item.EncodedSize > budget)
                break;
            budget -= item.EncodedSize;
            page.Add(item);
        }
        return new HookListReply { Entries = page, HasMore = index < entries.Count };
    }

    private delegate T BodyReader<out T>(ref HookIpcReader reader);

    private static T Read<T>(ReadOnlyMemory<byte> request, BodyReader<T> read)
    {
        var reader = new HookIpcReader(request.Span);
        return read(ref reader);
    }

    /// <summary>Joins the mapping's remote folder and a hook relative path ('\' separated) into a share path.</summary>
    private string ToRemotePath(ushort mappingIndex, string relativePath)
    {
        if (mappingIndex >= _mappings.Count)
            throw new ProtocolStatusException(NtStatus.InvalidParameter, $"Unknown mapping {mappingIndex}.");

        string[] segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".."))
            throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Relative path must be normalized.");

        string root = _mappings[mappingIndex].RemotePath.Trim('/');
        string relative = string.Join('/', segments);
        return root.Length == 0 ? relative : relative.Length == 0 ? root : root + "/" + relative;
    }

    /// <summary>Pipe connections of one client and the process behind them (under the clients lock).</summary>
    private sealed class ClientState(ProcessKey key)
    {
        public ProcessKey Key { get; } = key;

        public int Connections { get; set; }

        /// <summary>The process is watched: what it holds is released when it exits, not with its last connection.</summary>
        public bool Watched { get; set; }
    }

    private static HookFileInfo ToFileInfo(FsEntry entry) => new()
    {
        Attributes = entry.Attributes,
        Size = entry.Size,
        AllocationSize = entry.AllocationSize ?? (entry.IsDirectory ? 0 : (entry.Size + AllocationUnit - 1) / AllocationUnit * AllocationUnit),
        CreationTime = entry.CreationTime,
        LastAccessTime = entry.LastAccessTime,
        LastWriteTime = entry.LastWriteTime,
        ChangeTime = entry.ChangeTime,
        FileId = entry.FileId,
    };
}
