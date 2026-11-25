using System.Buffers.Binary;
using System.Security.Cryptography;
using ProcBridge.Crypto.Noise;

namespace ProcBridge.Crypto.Sessions;

/// <summary>
/// AEAD framing for one ordered connection (TCP or WebSocket) of a <see cref="SecureSession"/>.
/// <para>
/// Wire frame: <c>[counter: 8 bytes big-endian][ciphertext][tag: 16 bytes]</c>. ChaCha20-Poly1305
/// with nonce = 4 zero bytes + counter little-endian (Noise ChaChaPoly). Associated data =
/// counter + channel id + the caller's associated data (for example the clear routing header).
/// </para>
/// <para>
/// Anti-replay: the transport is ordered, so the receiver accepts exactly the next counter.
/// A replayed, dropped or reordered frame is rejected. A failed frame does not advance the counter.
/// Rekey: every <see cref="DefaultRekeyInterval"/> frames both sides replace the direction key with
/// Noise REKEY(k), so an old key leaking does not expose later frames. The counter keeps growing.
/// </para>
/// <para>
/// Threading: Seal and Open are thread-safe (separate locks per direction), but Seal only orders
/// counters, not the caller's later write. Two threads that Seal then write can put frames on the
/// wire out of counter order, and the receiver then rejects the stream for good. Concurrent senders
/// must use <see cref="SealAndWriteAsync"/>, which holds one gate across sealing and writing; do not
/// mix it with direct Seal calls on the same channel.
/// </para>
/// The channel owns its keys.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int KeySize = 32;
    public const int CounterSize = sizeof(ulong);
    public const int TagSize = CipherState.TagSize;

    /// <summary>Bytes added to every frame.</summary>
    public const int Overhead = CounterSize + TagSize;

    public const ulong DefaultRekeyInterval = 1UL << 20;

    private readonly DirectionState _send;
    private readonly DirectionState _receive;
    private readonly ulong _rekeyInterval;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _writeFaulted;

    internal SecureChannel(uint id, byte[] sendKey, byte[] receiveKey, ulong rekeyInterval)
    {
        if (rekeyInterval == 0)
            throw new ArgumentOutOfRangeException(nameof(rekeyInterval));
        Id = id;
        _rekeyInterval = rekeyInterval;
        _send = new DirectionState(sendKey);
        _receive = new DirectionState(receiveKey);
    }

    public uint Id { get; }

    /// <summary>Number of frames sealed so far.</summary>
    public ulong SentFrames { get { lock (_send.Lock) return _send.Counter; } }

    /// <summary>Number of frames opened so far.</summary>
    public ulong ReceivedFrames { get { lock (_receive.Lock) return _receive.Counter; } }

    public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var frame = new byte[plaintext.Length + Overhead];
        Seal(plaintext, associatedData, frame);
        return frame;
    }

    /// <summary>Encrypts into <paramref name="destination"/>; returns the frame length (plaintext + <see cref="Overhead"/>).</summary>
    public int Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData, Span<byte> destination)
    {
        var frameLength = plaintext.Length + Overhead;
        if (destination.Length < frameLength)
            throw new ArgumentException("Destination is too small.", nameof(destination));
        lock (_send.Lock)
        {
            var counter = _send.Counter;
            if (counter == ulong.MaxValue)
                throw new CryptoProtocolException("Channel frame counter exhausted; open a new channel.");
            _send.RekeyIfDue(counter, _rekeyInterval);
            BinaryPrimitives.WriteUInt64BigEndian(destination, counter);
            Span<byte> nonce = stackalloc byte[CipherState.NonceSize];
            CipherState.WriteNonce(nonce, counter);
            var ad = BuildAssociatedData(counter, associatedData);
            _send.Aead.Encrypt(
                nonce,
                plaintext,
                destination.Slice(CounterSize, plaintext.Length),
                destination.Slice(CounterSize + plaintext.Length, TagSize),
                ad);
            _send.Counter = counter + 1;
        }
        return frameLength;
    }

    /// <summary>
    /// Seals one frame and hands it to <paramref name="write"/> while holding a per-channel gate,
    /// so frames reach the transport in counter order even with many concurrent senders.
    /// The delegate gets the whole frame (it adds its own transport framing, if any) and must finish
    /// writing before it returns. If it throws, the frame counter was already used, so the channel is
    /// marked broken and later calls throw; close the connection.
    /// </summary>
    /// <exception cref="InvalidOperationException">A previous write failed.</exception>
    public async ValueTask SealAndWriteAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> associatedData,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writeFaulted)
                throw new InvalidOperationException("A previous write failed; the channel is broken.");
            var frame = new byte[plaintext.Length + Overhead];
            Seal(plaintext.Span, associatedData.Span, frame);
            try
            {
                await write(frame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _writeFaulted = true;
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <exception cref="CryptoProtocolException">Frame is forged, corrupted, replayed or out of order.</exception>
    public byte[] Open(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> associatedData = default)
    {
        if (frame.Length < Overhead)
            throw new CryptoProtocolException("Frame is too short.");
        var plaintext = new byte[frame.Length - Overhead];
        Open(frame, associatedData, plaintext);
        return plaintext;
    }

    /// <summary>Decrypts into <paramref name="destination"/>; returns the plaintext length.</summary>
    /// <exception cref="CryptoProtocolException">Frame is forged, corrupted, replayed or out of order.</exception>
    public int Open(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> associatedData, Span<byte> destination)
    {
        if (frame.Length < Overhead)
            throw new CryptoProtocolException("Frame is too short.");
        var plainLength = frame.Length - Overhead;
        if (destination.Length < plainLength)
            throw new ArgumentException("Destination is too small.", nameof(destination));
        var counter = BinaryPrimitives.ReadUInt64BigEndian(frame);
        lock (_receive.Lock)
        {
            var expected = _receive.Counter;
            if (counter < expected)
                throw new CryptoProtocolException($"Replayed frame {counter} (expected {expected}).");
            if (counter > expected)
                throw new CryptoProtocolException($"Out-of-order frame {counter} (expected {expected}).");
            if (counter == ulong.MaxValue)
                throw new CryptoProtocolException("Channel frame counter exhausted.");
            _receive.RekeyIfDue(counter, _rekeyInterval);
            Span<byte> nonce = stackalloc byte[CipherState.NonceSize];
            CipherState.WriteNonce(nonce, counter);
            var ad = BuildAssociatedData(counter, associatedData);
            try
            {
                _receive.Aead.Decrypt(
                    nonce,
                    frame.Slice(CounterSize, plainLength),
                    frame.Slice(CounterSize + plainLength, TagSize),
                    destination[..plainLength],
                    ad);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                destination[..plainLength].Clear();
                throw new CryptoProtocolException("Frame authentication failed.", ex);
            }
            _receive.Counter = counter + 1;
        }
        return plainLength;
    }

    private byte[] BuildAssociatedData(ulong counter, ReadOnlySpan<byte> associatedData)
    {
        var ad = new byte[CounterSize + sizeof(uint) + associatedData.Length];
        BinaryPrimitives.WriteUInt64BigEndian(ad, counter);
        BinaryPrimitives.WriteUInt32BigEndian(ad.AsSpan(CounterSize), Id);
        associatedData.CopyTo(ad.AsSpan(CounterSize + sizeof(uint)));
        return ad;
    }

    public void Dispose()
    {
        lock (_send.Lock)
            _send.Dispose();
        lock (_receive.Lock)
            _receive.Dispose();
    }

    /// <summary>Key, AEAD instance and counter of one direction; guarded by <see cref="Lock"/>.</summary>
    private sealed class DirectionState : IDisposable
    {
        private byte[] _key;
        private ulong _rekeyedAt;

        public DirectionState(byte[] key)
        {
            _key = (byte[])key.Clone();
            Aead = new ChaCha20Poly1305(_key);
        }

        public Lock Lock { get; } = new();

        public ChaCha20Poly1305 Aead { get; private set; }

        public ulong Counter { get; set; }

        /// <summary>Ratchets once when the counter crosses a multiple of the interval (deterministic on both sides).</summary>
        public void RekeyIfDue(ulong counter, ulong interval)
        {
            if (counter == 0 || counter % interval != 0 || _rekeyedAt == counter)
                return;
            var newKey = CipherState.Rekey(_key);
            CryptographicOperations.ZeroMemory(_key);
            _key = newKey;
            Aead.Dispose();
            Aead = new ChaCha20Poly1305(_key);
            _rekeyedAt = counter;
        }

        public void Dispose()
        {
            Aead.Dispose();
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
