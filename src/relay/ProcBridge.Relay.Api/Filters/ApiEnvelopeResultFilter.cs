using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Shared.Common;

namespace ProcBridge.Relay.Api.Filters;

/// <summary>
/// Wraps what an action returns into <see cref="ApiResponse{T}"/>. Actions return the VM directly (they implement the
/// Integration.Api interfaces), so nobody wraps by hand. Non-2xx results (already an error body) pass through.
/// </summary>
public sealed class ApiEnvelopeResultFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var requestId = context.HttpContext.Items[RequestIdKeys.ItemKey]?.ToString() ?? string.Empty;
        context.Result = context.Result switch
        {
            ObjectResult { StatusCode: >= 300 } or ObjectResult { Value: ApiErrorResponse } => context.Result,
            ObjectResult o => new ObjectResult(ApiResponse<object?>.Ok(o.Value, requestId)) { StatusCode = StatusCodes.Status200OK },
            EmptyResult => new ObjectResult(ApiResponse<object?>.Ok(null, requestId)) { StatusCode = StatusCodes.Status200OK },
            _ => context.Result,
        };
        return next();
    }
}
