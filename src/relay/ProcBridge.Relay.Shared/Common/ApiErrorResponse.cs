namespace ProcBridge.Relay.Shared.Common;

/// <summary>Error envelope: <c>{ requestId, error: { code, message } }</c>.</summary>
public sealed class ApiErrorResponse
{
    public string RequestId { get; init; } = string.Empty;
    public required ApiError Error { get; init; }
}
