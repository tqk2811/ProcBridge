using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 GroupCount | u16 Reserved | GroupCount × HookWatchChanges</c>. Only watches with changes (or lost
/// changes) appear; no group at all is a timeout or a new watch of the process. Changes that do not fit stay queued
/// for the next poll.
/// </summary>
public sealed class HookWaitAnyChangesReply : IHookIpcMessage
{
    /// <summary>Bytes before the first group.</summary>
    public const int FixedSize = 4;

    public required IReadOnlyList<HookWatchChanges> Watches { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.WaitAnyChanges;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16((ushort)Watches.Count);
        writer.WriteUInt16(0);
        foreach (HookWatchChanges watch in Watches)
        {
            writer.WriteUInt32(watch.WatchId);
            writer.WriteUInt16((ushort)watch.Changes.Count);
            writer.WriteUInt16(watch.Overflow ? HookIpcProtocol.ChangesFlagOverflow : (ushort)0);
            foreach (HookChange change in watch.Changes)
            {
                writer.WriteUInt32(change.Action);
                writer.WriteString(change.Name);
            }
        }
    }

    public static HookWaitAnyChangesReply Read(ref HookIpcReader reader)
    {
        int groups = reader.ReadUInt16();
        reader.ReadUInt16();
        var watches = new HookWatchChanges[groups];
        for (int g = 0; g < groups; g++)
        {
            uint id = reader.ReadUInt32();
            int count = reader.ReadUInt16();
            ushort flags = reader.ReadUInt16();
            var changes = new HookChange[count];
            for (int i = 0; i < count; i++)
            {
                uint action = reader.ReadUInt32();
                changes[i] = new HookChange(action, reader.ReadString());
            }
            watches[g] = new HookWatchChanges(id, changes, (flags & HookIpcProtocol.ChangesFlagOverflow) != 0);
        }
        return new HookWaitAnyChangesReply { Watches = watches };
    }
}
