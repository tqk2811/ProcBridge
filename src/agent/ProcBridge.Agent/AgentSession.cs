using System.Buffers;
using System.Net.Sockets;
using ProcBridge.Agent.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Helpers;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Serves one connection: Hello first, then requests handled concurrently (bounded per connection) and answered
/// out of order by request id. Owns the channel and the socket.
/// </summary>
internal sealed class AgentSession
{
    private readonly IFrameChannel _channel;
    private readonly TcpClient _client;
    private readonly IReadOnlyDictionary<string, ReadOnlyShare> _shares;
    private readonly AgentOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private ReadOnlyShare? _share;

    public AgentSession(IFrameChannel channel, TcpClient client, IReadOnlyDictionary<string, ReadOnlyShare> shares, AgentOptions options)
    {
        _channel = channel;
        _client = client;
        _shares = shares;
        _options = options;
        _concurrency = new SemaphoreSlim(options.MaxConcurrentRequestsPerConnection);
    }

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
            if (!_shares.TryGetValue(request.ShareName, out _share))
                throw new ProtocolStatusException(NtStatus.BadNetworkName, $"Unknown share '{request.ShareName}'.");

            var response = new HelloResponse
            {
                ProtocolVersion = ProtocolInfo.Version,
                ServerName = _options.ServerName,
                MaxFrameSize = _options.MaxFrameSize,
                MaxReadSize = _options.MaxReadSize,
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
                    var response = new FsStatResponse { Entry = _share!.Stat(stat.Path) };
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsList:
                {
                    FsListRequest list = ProtocolSerializer.Deserialize<FsListRequest>(request.Header);
                    FsListResponse response = _share!.List(list.Path, list.StartIndex, list.MaxCount, _options.MaxFrameSize - Frame.FixedBodySize);
                    await SendAsync(FrameKind.Response, request, ProtocolSerializer.Serialize(response), default).ConfigureAwait(false);
                    break;
                }
                case MessageType.FsRead:
                    await HandleReadAsync(request).ConfigureAwait(false);
                    break;
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

    private async Task HandleReadAsync(Frame request)
    {
        FsReadRequest read = ProtocolSerializer.Deserialize<FsReadRequest>(request.Header);
        if (read.Length < 0 || read.Length > _options.MaxReadSize)
            throw new ProtocolStatusException(NtStatus.InvalidParameter, $"Read length {read.Length} exceeds {_options.MaxReadSize}.");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(read.Length, 1));
        try
        {
            int count = _share!.Read(read.Path, read.Offset, buffer.AsSpan(0, read.Length), read.ExpectedVersion, out FsVersion version);
            byte[] header = ProtocolSerializer.Serialize(new FsReadResponse { Version = version });
            await SendAsync(FrameKind.Response, request, header, buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Task SendErrorAsync(Frame request, Exception exception)
    {
        var error = new ErrorResponse { Status = NtStatusMapper.FromException(exception), Message = exception.Message };
        return SendAsync(FrameKind.Error, request, ProtocolSerializer.Serialize(error), default);
    }

    private async Task SendAsync(FrameKind kind, Frame request, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload)
    {
        var frame = new Frame { Kind = kind, MessageType = request.MessageType, RequestId = request.RequestId, Header = header, Payload = payload };
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
