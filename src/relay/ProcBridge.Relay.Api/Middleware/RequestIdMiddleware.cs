using ProcBridge.Relay.Api.Infrastructure;

namespace ProcBridge.Relay.Api.Middleware;

/// <summary>Reads or creates <c>X-Request-Id</c>, keeps it in <c>HttpContext.Items</c> and echoes it in the response.</summary>
public sealed class RequestIdMiddleware
{
    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[RequestIdKeys.HeaderName].ToString();
        var requestId = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64 ? incoming : "req_" + Guid.NewGuid().ToString("N");
        context.Items[RequestIdKeys.ItemKey] = requestId;
        context.Response.Headers[RequestIdKeys.HeaderName] = requestId;
        return _next(context);
    }
}
