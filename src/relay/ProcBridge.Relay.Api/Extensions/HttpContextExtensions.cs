namespace ProcBridge.Relay.Api.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// Client address for limits and logs: after <c>UseForwardedHeaders</c> this is the address the trusted proxy saw.
    /// </summary>
    public static string ClientAddress(this HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
