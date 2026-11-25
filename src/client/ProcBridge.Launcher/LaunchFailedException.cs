using ProcBridge.Launcher.Enums;

namespace ProcBridge.Launcher;

/// <summary>
/// The run was stopped before the target started, for a reason the user can fix (<see cref="Reason"/>). The message is
/// the console text; the WPF Launcher words it from <see cref="Reason"/> and <see cref="Detail"/>.
/// </summary>
public sealed class LaunchFailedException : Exception
{
    public LaunchFailedException(LaunchFailureReason reason, string target, string detail, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
        Target = target;
        Detail = detail;
    }

    public LaunchFailureReason Reason { get; }

    /// <summary>What was missing: the Agent address or relay, the share name, or the working directory.</summary>
    public string Target { get; }

    /// <summary>Underlying error text (connection failure), or empty.</summary>
    public string Detail { get; }
}
