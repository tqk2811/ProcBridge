namespace ProcBridge.Launcher.Enums;

/// <summary>Why a run stopped before the target was started (<see cref="LaunchFailedException"/>).</summary>
public enum LaunchFailureReason
{
    /// <summary>No connection to the Agent: wrong address or relay, Agent stopped, firewall, or not paired any more.</summary>
    AgentUnreachable,

    /// <summary>The Agent answered but has no share of that name.</summary>
    ShareUnknown,

    /// <summary>The working directory exists neither on this machine nor (inside a virtual folder) on machine B.</summary>
    WorkingDirectoryMissing,
}
