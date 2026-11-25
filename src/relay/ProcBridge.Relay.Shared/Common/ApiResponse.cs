namespace ProcBridge.Relay.Shared.Common;

/// <summary>Success envelope of every REST response: <c>{ requestId, data }</c>.</summary>
public sealed class ApiResponse<T>
{
    public string RequestId { get; init; } = string.Empty;
    public T? Data { get; init; }

    public static ApiResponse<T> Ok(T data, string requestId) => new() { RequestId = requestId, Data = data };
}
