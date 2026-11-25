namespace ProcBridge.Relay.Shared.Common;

/// <summary>Error body: <see cref="Code"/> is UPPER_SNAKE (data on the wire), <see cref="Message"/> is for humans.</summary>
public sealed class ApiError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}
