using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol;

/// <summary>Failure carrying the NTSTATUS that should reach the hooked process.</summary>
public sealed class ProtocolStatusException : Exception
{
    public ProtocolStatusException(NtStatus status, string? message, bool isConnectionFailure = false, Exception? inner = null)
        : base(message ?? status.ToString(), inner)
    {
        Status = status;
        IsConnectionFailure = isConnectionFailure;
    }

    public NtStatus Status { get; }

    /// <summary>
    /// True when the connection itself broke (not an answer from the peer). Idempotent requests may be retried on
    /// another connection.
    /// </summary>
    public bool IsConnectionFailure { get; }
}
