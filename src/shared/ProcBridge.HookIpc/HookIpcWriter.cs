using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ProcBridge.HookIpc;

/// <summary>Writes a message body after the header slot of a buffer.</summary>
public ref struct HookIpcWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public HookIpcWriter(Span<byte> buffer)
    {
        if (buffer.Length < HookIpcProtocol.HeaderSize)
            throw new HookIpcFormatException("Buffer smaller than the header.");
        _buffer = buffer;
        _position = HookIpcProtocol.HeaderSize;
    }

    /// <summary>Total message size so far (header included).</summary>
    public readonly int Position => _position;

    private Span<byte> Take(int length)
    {
        if (_buffer.Length - _position < length)
            throw new HookIpcFormatException($"Message exceeds {_buffer.Length} bytes.");
        Span<byte> span = _buffer.Slice(_position, length);
        _position += length;
        return span;
    }

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);

    public void WriteString(string value)
    {
        if (value.Length > ushort.MaxValue)
            throw new HookIpcFormatException("String longer than 65535 characters.");
        WriteUInt16((ushort)value.Length);
        Span<byte> target = Take(value.Length * 2);
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(target);
            return;
        }
        for (int i = 0; i < value.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(target[(i * 2)..], value[i]);
    }

    /// <summary>Byte count <see cref="WriteString"/> uses for <paramref name="value"/>.</summary>
    public static int GetStringSize(string value) => 2 + value.Length * 2;
}
