namespace ProcBridge.Protocol.Enums;

/// <summary>What machine B does when a write-back finds the file changed since machine A read it (plan section 10).</summary>
public enum ConflictPolicy : byte
{
    /// <summary>Default: machine B's file stays; A's content is saved next to it as <c>name.procbridge-conflict-&lt;time&gt;</c>.</summary>
    KeepBoth = 0,
    /// <summary>Machine A wins: its content replaces the file whatever changed on machine B.</summary>
    Overwrite = 1,
}
