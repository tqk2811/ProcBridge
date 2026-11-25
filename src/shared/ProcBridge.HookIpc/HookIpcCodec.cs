using System.Buffers.Binary;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;
using ProcBridge.HookIpc.Models;

namespace ProcBridge.HookIpc;

/// <summary>Encodes whole messages and decodes their header.</summary>
public static class HookIpcCodec
{
    /// <summary>Encodes a successful request or reply; returns the message size.</summary>
    public static int Encode(Span<byte> buffer, uint requestId, IHookIpcMessage message)
    {
        var writer = new HookIpcWriter(buffer);
        message.WriteBody(ref writer);
        int size = writer.Position;
        WriteHeader(buffer, new HookIpcHeader((uint)size, HookIpcProtocol.Version, message.MessageType, requestId, 0));
        return size;
    }

    /// <summary>Encodes a failed reply: header only, <paramref name="status"/> is the NTSTATUS for the hooked call.</summary>
    public static int EncodeError(Span<byte> buffer, HookIpcMessageType type, uint requestId, uint status)
    {
        WriteHeader(buffer, new HookIpcHeader(HookIpcProtocol.HeaderSize, HookIpcProtocol.Version, type, requestId, status));
        return HookIpcProtocol.HeaderSize;
    }

    public static HookIpcHeader ReadHeader(ReadOnlySpan<byte> message)
    {
        if (message.Length < HookIpcProtocol.HeaderSize)
            throw new HookIpcFormatException("Message shorter than the header.");
        var header = new HookIpcHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(message),
            BinaryPrimitives.ReadUInt16LittleEndian(message[4..]),
            (HookIpcMessageType)BinaryPrimitives.ReadUInt16LittleEndian(message[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(message[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(message[12..]));
        if (header.Size != message.Length)
            throw new HookIpcFormatException($"Header size {header.Size} does not match message length {message.Length}.");
        if (header.Version != HookIpcProtocol.Version)
            throw new HookIpcFormatException($"IPC version {header.Version} is not supported.");
        return header;
    }

    private static void WriteHeader(Span<byte> buffer, HookIpcHeader header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, header.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], header.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], (ushort)header.MessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], header.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], header.Status);
    }
}
