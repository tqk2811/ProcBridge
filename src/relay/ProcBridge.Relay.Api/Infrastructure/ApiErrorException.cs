namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>HTTP error raised by controllers; <c>ExceptionWrapperMiddleware</c> writes it as the error envelope.</summary>
public sealed class ApiErrorException : Exception
{
    private ApiErrorException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }

    public static ApiErrorException BadRequest(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);
    public static ApiErrorException Unauthorized(string code, string message) => new(StatusCodes.Status401Unauthorized, code, message);
}
