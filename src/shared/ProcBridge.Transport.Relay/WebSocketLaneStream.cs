using System.Net.WebSockets;

namespace ProcBridge.Transport.Relay;

/// <summary>
/// One relay lane as a byte stream: each write is one binary WebSocket message, reads run across message boundaries
/// (the layer above frames its own data). A close from the other end reads as end of stream. Owns the socket.
/// One reader and one writer at a time, like a <see cref="System.Net.Sockets.NetworkStream"/>.
/// </summary>
internal sealed class WebSocketLaneStream : Stream
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly WebSocket _socket;
    private readonly Action? _onClosed;
    private int _disposed;

    /// <param name="onClosed">Called once when the stream is disposed (gives the lane number back).</param>
    public WebSocketLaneStream(WebSocket socket, Action? onClosed = null)
    {
        _socket = socket;
        _onClosed = onClosed;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return 0;
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
            {
                throw new IOException($"Relay lane broken: {e.Message}", e);
            }
            if (result.MessageType == WebSocketMessageType.Close)
                return 0;
            // An empty data message carries nothing: keep reading instead of reporting end of stream.
            if (result.Count > 0)
                return result.Count;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return;
        try
        {
            await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            throw new IOException($"Relay lane broken: {e.Message}", e);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            CloseAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Starts the close handshake (the relay forwards it to the other end), then drops the socket.</summary>
    private async Task CloseAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _socket.Abort();
            _socket.Dispose();
            _onClosed?.Invoke();
        }
    }
}
