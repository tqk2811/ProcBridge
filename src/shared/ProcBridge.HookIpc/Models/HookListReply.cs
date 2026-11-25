using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 Count | u16 Flags | Count × (FileInfo | string Name)</c>. Flags bit 0 = more entries follow; the
/// hook asks again with <c>StartIndex</c> advanced by <c>Count</c>. Entries are sorted case-insensitively by name.
/// </summary>
public sealed class HookListReply : IHookIpcMessage
{
    /// <summary>Bytes before the first entry.</summary>
    public const int FixedSize = 4;

    public required IReadOnlyList<HookListEntry> Entries { get; init; }
    public required bool HasMore { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.List;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16((ushort)Entries.Count);
        writer.WriteUInt16(HasMore ? HookIpcProtocol.ListFlagHasMore : (ushort)0);
        foreach (HookListEntry entry in Entries)
        {
            entry.Info.Write(ref writer);
            writer.WriteString(entry.Name);
        }
    }

    public static HookListReply Read(ref HookIpcReader reader)
    {
        int count = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        var entries = new HookListEntry[count];
        for (int i = 0; i < count; i++)
        {
            HookFileInfo info = HookFileInfo.Read(ref reader);
            entries[i] = new HookListEntry(info, reader.ReadString());
        }
        return new HookListReply { Entries = entries, HasMore = (flags & HookIpcProtocol.ListFlagHasMore) != 0 };
    }
}
