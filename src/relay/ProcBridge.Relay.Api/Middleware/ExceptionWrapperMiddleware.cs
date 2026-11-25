using Newtonsoft.Json;

using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Shared.Common;
using ProcBridge.Relay.Shared.Json;

namespace ProcBridge.Relay.Api.Middleware;

/// <summary>Turns exceptions into the error envelope with the right HTTP status.</summary>
public sealed class ExceptionWrapperMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionWrapperMiddleware> _logger;
    private readonly JsonSerializerSettings _json = RelayJsonSettings.Create();

    public ExceptionWrapperMiddleware(RequestDelegate next, ILogger<ExceptionWrapperMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away: nobody to answer.
        }
        catch (Exception ex)
        {
            var requestId = context.Items[RequestIdKeys.ItemKey]?.ToString() ?? string.Empty;
            var (status, code, message) = ex switch
            {
                ApiErrorException api => (api.StatusCode, api.Code, api.Message),
                ServiceException service => (service.StatusCode, service.Code, service.Message),
                _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "Lỗi máy chủ, mã yêu cầu " + requestId + "."),
            };
            if (status >= 500)
                _logger.LogError(ex, "Lỗi xử lý {Method} {Path} ({RequestId}).", context.Request.Method, context.Request.Path, requestId);
            else
                _logger.LogWarning("{Method} {Path} -> {Status} {Code}: {Message} ({RequestId}).", context.Request.Method, context.Request.Path, status, code, message, requestId);

            if (context.Response.HasStarted)
                return;
            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var body = new ApiErrorResponse { RequestId = requestId, Error = new ApiError { Code = code, Message = message } };
            await context.Response.WriteAsync(JsonConvert.SerializeObject(body, _json));
        }
    }
}
