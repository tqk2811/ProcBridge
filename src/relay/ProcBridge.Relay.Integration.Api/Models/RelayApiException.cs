namespace ProcBridge.Relay.Integration.Api.Models;

/// <summary>The relay answered with an error envelope (or a non-JSON failure).</summary>
public sealed class RelayApiException : Exception
{
    public RelayApiException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}
