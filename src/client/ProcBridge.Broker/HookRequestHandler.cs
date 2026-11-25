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
public sealed class HookRequestHandler
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

    private readonly FileCache _cache;
    private readonly IReadOnlyList<VirtualMapping> _mappings;
    private readonly WriteBackQueue? _writeBack;
    private readonly bool _writeThrough;

    /// <param name="writeBack">Write-back queue; null serves the share read only whatever the Agent allows (tests).</param>
    /// <param name="writeThrough">Tell hooks to flush after every write (<see cref="Models.BrokerOptions.WriteThrough"/>).</param>
    public HookRequestHandler(FileCache cache, IReadOnlyList<VirtualMapping> mappings, WriteBackQueue? writeBack = null, bool writeThrough = false)
    {
        if (mappings.Count > ushort.MaxValue)
            throw new ArgumentException("Too many mappings.", nameof(mappings));
        _cache = cache;
        _mappings = mappings;
        _writeBack = writeBack;
        _writeThrough = writeThrough;
    }

    /// <summary>Handles <paramref name="request"/> and writes the reply into <paramref name="reply"/>; returns its size.</summary>
    public async Task<int> HandleAsync(ReadOnlyMemory<byte> request, Memory<byte> reply, CancellationToken cancellationToken)
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
            IHookIpcMessage response = await DispatchAsync(header.MessageType, request, cancellationToken).ConfigureAwait(false);
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

    private async Task<IHookIpcMessage> DispatchAsync(HookIpcMessageType type, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case HookIpcMessageType.Hello:
                Read(request, HookHelloRequest.Read);
                return new HookHelloReply
                {
                    VirtualRoots = _mappings.Select(x => x.VirtualRoot).ToArray(),
                    Flags = _writeThrough ? HookIpcProtocol.HelloFlagWriteThrough : (ushort)0,
                };

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
                    return await ResolveWritableAsync(resolve, remotePath, cancellationToken).ConfigureAwait(false);
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
                    CachedItem item = await _cache.EnsureAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    return new HookResolveReply { Info = ToFileInfo(item.Entry), CachePath = item.LocalPath };
                }
                catch (ProtocolStatusException e) when (resolve.CreateDisposition == FileOpenIf && e.Status == NtStatus.ObjectNameNotFound)
                {
                    // FILE_OPEN_IF would create the missing file.
                    throw new ProtocolStatusException(NtStatus.MediaWriteProtected, "Share is read only.");
                }
            }

            case HookIpcMessageType.List:
            {
                HookListRequest list = Read(request, HookListRequest.Read);
                IReadOnlyList<FsEntry> entries = await _cache.ListAsync(ToRemotePath(list.MappingIndex, list.RelativePath), cancellationToken).ConfigureAwait(false);
                return BuildListPage(entries, (int)Math.Min(list.StartIndex, int.MaxValue));
            }

            case HookIpcMessageType.Close:
            {
                HookTokenRequest close = Read(request, (ref HookIpcReader r) => HookTokenRequest.Read(ref r, type));
                if (_writeBack is not null)
                    await _writeBack.CloseAsync(close.WriteToken, cancellationToken).ConfigureAwait(false);
                return new HookEmptyReply { Type = type };
            }

            case HookIpcMessageType.Flush:
            {
                HookTokenRequest flush = Read(request, (ref HookIpcReader r) => HookTokenRequest.Read(ref r, type));
                if (_writeBack is not null)
                    await _writeBack.FlushAsync(flush.WriteToken, cancellationToken).ConfigureAwait(false);
                return new HookEmptyReply { Type = type };
            }

            case HookIpcMessageType.SetInfo:
            {
                HookSetInfoRequest set = Read(request, HookSetInfoRequest.Read);
                string remotePath = ToRemotePath(set.MappingIndex, set.RelativePath);
                await RequireWritableAsync(cancellationToken).ConfigureAwait(false);
                var change = new FsSetInfoRequest
                {
                    Path = remotePath,
                    Attributes = set.Fields.HasFlag(HookSetInfoFields.Attributes) ? set.Attributes : null,
                    CreationTime = set.Fields.HasFlag(HookSetInfoFields.CreationTime) ? set.CreationTime : null,
                    LastAccessTime = set.Fields.HasFlag(HookSetInfoFields.LastAccessTime) ? set.LastAccessTime : null,
                    LastWriteTime = set.Fields.HasFlag(HookSetInfoFields.LastWriteTime) ? set.LastWriteTime : null,
                    EndOfFile = set.Fields.HasFlag(HookSetInfoFields.EndOfFile) ? set.EndOfFile : null,
                };
                FsEntry entry = await _cache.SetInfoAsync(change, cancellationToken).ConfigureAwait(false);
                _writeBack!.InfoChanged(remotePath, entry);
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
                CachedItem item = await _cache.RenameAsync(remotePath, newRemotePath, replace, cancellationToken).ConfigureAwait(false);
                _writeBack!.Renamed(remotePath, newRemotePath);
                return new HookRenameReply { Info = ToFileInfo(item.Entry), NewCachePath = item.LocalPath };
            }

            case HookIpcMessageType.Delete:
            {
                HookDeleteRequest delete = Read(request, HookDeleteRequest.Read);
                string remotePath = ToRemotePath(delete.MappingIndex, delete.RelativePath);
                await RequireWritableAsync(cancellationToken).ConfigureAwait(false);
                _writeBack!.Deleted(remotePath);
                await _cache.DeleteAsync(remotePath, (delete.Flags & HookDeleteRequest.FlagKeepItem) != 0, cancellationToken).ConfigureAwait(false);
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
    private async Task<IHookIpcMessage> ResolveWritableAsync(HookResolveRequest resolve, string remotePath, CancellationToken cancellationToken)
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
                return await ResolveWritableAsync(resolve, remotePath, cancellationToken).ConfigureAwait(false);
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
            item = await _cache.EnsureAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        ulong token = 0;
        bool contentWrite = (resolve.DesiredAccess & ContentWriteMask) != 0 || resolve.CreateDisposition is FileSupersede or FileOverwrite or FileOverwriteIf
            || (created && resolve.CreateDisposition != FileOpen);
        if (contentWrite && !item.Entry.IsDirectory)
            token = _writeBack!.Open(remotePath, item.Entry.Version);
        return new HookResolveReply { Info = ToFileInfo(item.Entry), CachePath = item.LocalPath, WriteToken = token };
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
