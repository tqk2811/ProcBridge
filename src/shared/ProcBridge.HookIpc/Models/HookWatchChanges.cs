namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Changes of one watch in a WaitAnyChanges reply: <c>u32 WatchId | u16 Count | u16 Flags | Count × (u32 Action | string Name)</c>.
/// Flags bit 0 (<see cref="HookIpcProtocol.ChangesFlagOverflow"/>) means changes of this watch were lost.
/// </summary>
public sealed record HookWatchChanges(uint WatchId, IReadOnlyList<HookChange> Changes, bool Overflow)
{
    /// <summary>Bytes before the first change of the group.</summary>
    public const int FixedSize = 8;
}
