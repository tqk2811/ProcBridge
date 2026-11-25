using ProcBridge.Transport.Secure.Interfaces;

namespace ProcBridge.Tests.Secure;

/// <summary>
/// Connector that copies every byte sent and received on its streams into one capture, like a packet capture on the
/// wire between machine A and machine B. Wraps another connector (borrowed).
/// </summary>
internal sealed class WireTap : IStreamConnector
{
    private readonly IStreamConnector _inner;
    private readonly MemoryStream _capture = new();

    public WireTap(IStreamConnector inner)
    {
        _inner = inner;
    }

    public string Description => _inner.Description;

    public byte[] Captured
    {
        get
        {
            lock (_capture)
                return _capture.ToArray();
        }
    }

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
        => new TeeStream(await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false), this);

    private void Record(ReadOnlySpan<byte> data)
    {
        lock (_capture)
            _capture.Write(data);
    }

    /// <summary>Stream that records what goes through it. Owns the inner stream.</summary>
    private sealed class TeeStream(Stream inner, WireTap tap) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            tap.Record(buffer.Span[..read]);
            return read;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            tap.Record(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            tap.Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            tap.Record(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
