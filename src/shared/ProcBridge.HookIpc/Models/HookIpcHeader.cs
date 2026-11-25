using ProcBridge.HookIpc.Enums;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Fixed 16-byte header: <c>u32 Size | u16 Version | u16 MessageType | u32 RequestId | u32 Status</c>.
/// <see cref="Size"/> counts the whole message, header included.
/// </summary>
public readonly record struct HookIpcHeader(uint Size, ushort Version, HookIpcMessageType MessageType, uint RequestId, uint Status);
