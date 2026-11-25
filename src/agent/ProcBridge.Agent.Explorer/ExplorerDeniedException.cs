using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer;

/// <summary>Thrown inside providers to stop a request with a specific status.</summary>
internal sealed class ExplorerDeniedException : Exception
{
    public ExplorerDeniedException(ExplorerStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public ExplorerStatus Status { get; }
}
