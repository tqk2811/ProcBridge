using System.Buffers.Binary;

namespace ProcBridge.HookIpc;

/// <summary>Reads a message body; starts right after the header.</summary>
public ref struct HookIpcReader
{
    private readonly ReadOnlySpan<byte> _message;
    private int _position;

    public HookIpcReader(ReadOnlySpan<byte> message)
    {
        if (message.Length < HookIpcProtocol.HeaderSize)
            throw new HookIpcFormatException("Message shorter than the header.");
        _message = message;
        _position = HookIpcProtocol.HeaderSize;
    }

    public readonly int Remaining => _message.Length - _position;

    private ReadOnlySpan<byte> Take(int length)
    {
        if (Remaining < length)
            throw new HookIpcFormatException("Message is truncated.");
        ReadOnlySpan<byte> span = _message.Slice(_position, length);
        _position += length;
        return span;
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public string ReadString()
    {
        int length = ReadUInt16();
        ReadOnlySpan<byte> bytes = Take(length * 2);
        return string.Create(length, bytes.ToArray(), static (chars, source) =>
        {
            for (int i = 0; i < chars.Length; i++)
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(i * 2));
        });
    }
}
