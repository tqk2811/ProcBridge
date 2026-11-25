using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using ProcBridge.Agent.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Helpers;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Serves one connection: Hello first, then requests handled concurrently (bounded per connection) and answered
/// out of order by request id. A Hello with an empty share name opens a connection for the Remote Explorer only.
/// Owns the channel and the socket.
/// </summary>
internal sealed class AgentSession
{
    private readonly IFrameChannel _channel;
    private readonly TcpClient _client;
    private readonly IReadOnlyDictionary<string, AgentShare> _shares;
    private readonly AgentOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private readonly Func<AgentShare, ShareWatcher> _getWatcher;
    private readonly ConcurrentDictionary<ulong, ShareTreeWalk> _walks = new();
    private readonly object _watchLock = new();
    private Channel<FsChangeNotification>? _notifications;
    private IDisposable? _watchSubscription;
    private Task _notificationPump = Task.CompletedTask;
    private int _lostNotifications;
    private long _nextWalkId;
    private readonly ExplorerDispatcher? _explorer;
    private AgentShare? _share;

    /// <param name="explorer">Remote Explorer of the authenticated machine A; null (plain transport) refuses Explorer messages.</param>
    public AgentSession(IFrameChannel channel, TcpClient client, IReadOnlyDictionary<string, AgentShare> shares, AgentOptions options, Func<AgentShare, ShareWatcher> getWatcher, ExplorerDispatcher? explorer = null)
    {
        _explorer = explorer;
        _getWatcher = getWatcher;
        _channel = channel;
        _client = client;
        _shares = shares;
        _options = options;
        _concurrency = new SemaphoreSlim(options.MaxConcurrentRequestsPerConnection);
    }

    /// <summary>Share named in Hello; Explorer-only connections have none.</summary>
    private AgentShare Share => _share ?? throw new ProtocolStatusException(NtStatus.BadNetworkName, "This connection did not open a share.");

    /// <summary>Closes the socket, which ends <see cref="RunAsync"/>.</summary>
    public void Abort() => _client.Dispose();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var running = new List<Task>();
        try
        {
            if (!await HandshakeAsync(cancellationToken).ConfigureAwait(false))
                return;

            while (!cancellationToken.IsCancellationRequested)
            {
                Frame? frame = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;
                if (frame.Value.Kind != FrameKind.Request)
                    break;

                await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                running.RemoveAll(x => x.IsCompleted);
                running.Add(Task.Run(() => HandleAsync(frame.Value, cancellationToken), CancellationToken.None));
            }
        }
        finally
        {
            _client.Dispose();
            await Task.WhenAll(running).ConfigureAwait(false);
            StopWatching();
            await _notificationPump.ConfigureAwait(false);
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> HandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HelloTimeout);
        Frame? frame = await _channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        if (frame is not { Kind: FrameKind.Request, MessageType: MessageType.Hello } hello)
            return false;

        try
        {
            HelloRequest request = ProtocolSerializer.Deserialize<HelloRequest>(hello.Header);
            if (request.ProtocolVersion != ProtocolInfo.Version)
                throw new ProtocolStatusException(NtStatus.RevisionMismatch, $"Agent speaks protocol {ProtocolInfo.Version}.");
            if (request.ShareName.Length > 0 && !_shares.TryGetValue(request.ShareName, out _share))
                throw new ProtocolStatusException(NtStatus.BadNetworkName, $"Unknown share '{request.ShareName}'.");

            var response = new HelloResponse
            {
                ProtocolVersion = ProtocolInfo.Version,
                ServerName = _options.ServerName,
                MaxFrameSize = _options.MaxFrameSize,
                MaxReadSize = _options.MaxReadSize,
                Writable = _share?.IsWritable == true, // no share: Remote Explorer only connection
            };
            await SendAsync(FrameKind.Response, hello, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
            return true;
        }
        catch (ProtocolStatusException e)
        {
            await SendErrorAsync(hello, e).ConfigureAwait(false);
            return false;
        }
    }

    private async Task HandleAsync(Frame request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.MessageType)
            {
                case MessageType.FsStat:
                {
                    FsStatRequest stat = ProtocolSerializer.Deserialize<FsStatRequest>(request.Header);
                    var response = new FsStatResponse { Entry = Share.Stat(stat.Path) };
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsList:
                {
                    FsListRequest list = ProtocolSerializer.Deserialize<FsListRequest>(request.Header);
                    FsListResponse response = Share.List(list.Path, list.StartIndex, list.MaxCount, _options.MaxFrameSize - Frame.FixedBodySize);
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsWatch:
                {
                    ProtocolSerializer.Deserialize<FsWatchRequest>(request.Header);
                    StartWatching();
                    var response = new FsWatchResponse { BatchDelayMilliseconds = (int)_options.WatchBatchDelay.TotalMilliseconds };
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsTree:
                {
                    FsTreeRequest tree = ProtocolSerializer.Deserialize<FsTreeRequest>(request.Header);
                    FsTreeResponse response = NextTreePage(tree);
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsRead:
                    await HandleReadAsync(request).ConfigureAwait(false);
                    break;
                case var type when ExplorerDispatcher.IsExplorerMessage(type):
                {
                    if (_explorer is null)
                        throw new ProtocolStatusException(NtStatus.AccessDenied, "The Remote Explorer needs an authenticated, encrypted connection.");
                    (byte[] header, ReadOnlyMemory<byte> payload) = _explorer.Handle(request);
                    await SendAsync(FrameKind.Response, request, header, payload).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsCreate:
                {
                    FsCreateRequest create = ProtocolSerializer.Deserialize<FsCreateRequest>(request.Header);
                    await SendStatAsync(request, Share.Create(create.Path, create.IsDirectory, create.Attributes)).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsWrite:
                {
                    FsWriteRequest write = ProtocolSerializer.Deserialize<FsWriteRequest>(request.Header);
                    int count = Share.WriteBlock(write.Path, write.UploadId, write.Offset, request.Payload.Span);
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(new FsWriteResponse { Count = count }), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsCommit:
                {
                    FsCommitRequest commit = ProtocolSerializer.Deserialize<FsCommitRequest>(request.Header);
                    FsCommitResponse response = Share.Commit(commit.Path, commit.UploadId, commit.Size, commit.ExpectedVersion, commit.LastWriteTime, commit.Policy);
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsSetInfo:
                {
                    FsSetInfoRequest set = ProtocolSerializer.Deserialize<FsSetInfoRequest>(request.Header);
                    await SendStatAsync(request, Share.SetInfo(set.Path, set.Attributes, set.CreationTime, set.LastAccessTime, set.LastWriteTime, set.EndOfFile)).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsRename:
                {
                    FsRenameRequest rename = ProtocolSerializer.Deserialize<FsRenameRequest>(request.Header);
                    await SendStatAsync(request, Share.Rename(rename.Path, rename.NewPath, rename.ReplaceIfExists)).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsDelete:
                {
                    FsDeleteRequest delete = ProtocolSerializer.Deserialize<FsDeleteRequest>(request.Header);
                    var response = new FsDeleteResponse { Deleted = Share.Delete(delete.Path, delete.ExpectedFileId) };
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                default:
                    throw new ProtocolStatusException(NtStatus.NotImplemented, $"Message {request.MessageType} is not supported.");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            try
            {
                await SendErrorAsync(request, e).ConfigureAwait(false);
            }
            catch
            {
                // Connection is gone; the receive loop will notice.
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private FsTreeResponse NextTreePage(FsTreeRequest request)
    {
        ulong cursor = request.Cursor;
        ShareTreeWalk? walk;
        if (cursor == 0)
        {
            if (_walks.Count >= _options.MaxTreeWalksPerConnection)
                throw new ProtocolStatusException(NtStatus.InsufficientResources, "Too many unfinished tree walks on this connection.");
            int depth = request.MaxDepth > 0 ? request.MaxDepth : _options.DefaultTreeDepth;
            walk = Share.StartTree(request.Path, depth, _options.MaxTreeEntries);
            cursor = (ulong)Interlocked.Increment(ref _nextWalkId);
            _walks[cursor] = walk;
        }
        else if (!_walks.TryGetValue(cursor, out walk))
        {
            throw new ProtocolStatusException(NtStatus.InvalidParameter, $"Unknown tree cursor {cursor}.");
        }

        lock (walk)
        {
            FsTreeDirectory[] page;
            try
            {
                page = walk.NextPage(_options.MaxFrameSize - Frame.FixedBodySize);
            }
            catch
            {
                _walks.TryRemove(cursor, out _);
                throw;
            }
            if (walk.IsDone)
                _walks.TryRemove(cursor, out _);
            return new FsTreeResponse { Directories = page, Cursor = walk.IsDone ? 0 : cursor, Truncated = walk.Truncated };
        }
    }

    /// <summary>Subscribes this connection to the share watcher; a second FsWatch on the same connection is a no-op.</summary>
    private void StartWatching()
    {
        lock (_watchLock)
        {
            if (_notifications is not null)
                return;
            var queue = Channel.CreateBounded<FsChangeNotification>(new BoundedChannelOptions(_options.NotificationQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _notifications = queue;
            _notificationPump = Task.Run(() => PumpNotificationsAsync(queue.Reader));
            try
            {
                // onLost: the share's watcher broke and could not be restarted, so no more changes would reach this
                // client while it still trusts its cache. Closing the connection makes it fall back to its TTL and resubscribe.
                _watchSubscription = _getWatcher(Share).Subscribe(notification =>
                {
                    // Never block the watcher: a client that does not keep up loses the details and gets a rescan.
                    if (!queue.Writer.TryWrite(notification))
                        Interlocked.Exchange(ref _lostNotifications, 1);
                }, onLost: Abort);
            }
            catch
            {
                // Not subscribed (the share cannot be watched now): let a later FsWatch try again.
                _notifications = null;
                queue.Writer.TryComplete();
                throw;
            }
        }
    }

    private void StopWatching()
    {
        lock (_watchLock)
        {
            _watchSubscription?.Dispose();
            _watchSubscription = null;
            _notifications?.Writer.TryComplete();
        }
    }

    private async Task PumpNotificationsAsync(ChannelReader<FsChangeNotification> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out FsChangeNotification? notification))
                {
                    if (Interlocked.Exchange(ref _lostNotifications, 0) != 0)
                    {
                        // Whatever was dropped and whatever is still queued is covered by one rescan.
                        while (reader.TryRead(out _))
                        {
                        }
                        notification = new FsChangeNotification { Changes = [], Rescan = true };
                    }
                    await SendNotificationAsync(notification).ConfigureAwait(false);
                }
                if (Interlocked.Exchange(ref _lostNotifications, 0) != 0)
                    await SendNotificationAsync(new FsChangeNotification { Changes = [], Rescan = true }).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or ProtocolStatusException)
        {
            // Connection gone; RunAsync cleans up.
        }
    }

    private Task SendNotificationAsync(FsChangeNotification notification)
        => SendFrameAsync(new Frame { Kind = FrameKind.Notification, MessageType = MessageType.FsChanged, RequestId = 0, Header = ProtocolSerializer.Serialize(notification) });

    private async Task HandleReadAsync(Frame request)
    {
        FsReadRequest read = ProtocolSerializer.Deserialize<FsReadRequest>(request.Header);
        if (read.Length < 0 || read.Length > _options.MaxReadSize)
            throw new ProtocolStatusException(NtStatus.InvalidParameter, $"Read length {read.Length} exceeds {_options.MaxReadSize}.");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(read.Length, 1));
        try
        {
            int count = Share.Read(read.Path, read.Offset, buffer.AsSpan(0, read.Length), read.ExpectedVersion, out FsVersion version);
            byte[] header = ProtocolSerializer.Serialize(new FsReadResponse { Version = version });
            await SendAsync(FrameKind.Response, request, header, buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Task SendStatAsync(Frame request, FsEntry entry)
        => SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(new FsStatResponse { Entry = entry }), default);

    private Task SendErrorAsync(Frame request, Exception exception)
    {
        var error = new ErrorResponse { Status = NtStatusMapper.FromException(exception), Message = exception.Message };
        return SendAsync(FrameKind.Error, request, ProtocolSerializer.Serialize(error), default);
    }

    private Task SendAsync(FrameKind kind, Frame request, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload)
        => SendFrameAsync(new Frame { Kind = kind, MessageType = request.MessageType, RequestId = request.RequestId, Header = header, Payload = payload });

    private async Task SendFrameAsync(Frame frame)
    {
        using var timeout = new CancellationTokenSource(_options.SendTimeout);
        try
        {
            await _channel.SendAsync(frame, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
        {
            // The client stopped reading: drop the session instead of keeping a stuck writer (and its buffers) forever.
            Abort();
            throw new IOException("Client did not read the response in time.", e);
        }
    }
}
