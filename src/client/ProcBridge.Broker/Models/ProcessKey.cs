namespace ProcBridge.Broker.Models;

/// <summary>
/// One process for the lifetime of the session: its id and its creation time (FILETIME, UTC), so a process id the system
/// hands out again later is not taken for the old process. <see cref="CreationTime"/> 0 means unknown: such a key stands
/// for a pipe client the Broker cannot open (or one without a process id) and is never watched.
/// </summary>
public readonly record struct ProcessKey(int ProcessId, long CreationTime);
