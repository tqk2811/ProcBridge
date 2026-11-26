namespace ProcBridge.Broker.Enums;

/// <summary>What <see cref="ProcessWatch.Watch"/> found.</summary>
public enum ProcessWatchResult
{
    /// <summary>The process is alive and watched (or was already): its exit will be reported.</summary>
    Watching,

    /// <summary>The process is gone (or its id now belongs to another process); the exit is reported right away.</summary>
    Exited,

    /// <summary>The process cannot be opened (access denied, or a key without creation time): its exit is never reported.</summary>
    Unavailable,
}
