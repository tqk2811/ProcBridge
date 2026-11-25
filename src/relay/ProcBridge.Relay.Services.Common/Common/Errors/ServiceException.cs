namespace ProcBridge.Relay.Services.Common.Common.Errors;

/// <summary>
/// Business error with an HTTP status and an UPPER_SNAKE code. Services do not reference the Api host, so the host
/// middleware translates this into the error envelope.
/// </summary>
public sealed class ServiceException : Exception
{
    private ServiceException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }

    public static ServiceException BadRequest(string code, string message) => new(400, code, message);
    public static ServiceException Unauthorized(string code, string message) => new(401, code, message);
    public static ServiceException Forbidden(string code, string message) => new(403, code, message);
    public static ServiceException NotFound(string code, string message) => new(404, code, message);
    public static ServiceException Conflict(string code, string message) => new(409, code, message);
    public static ServiceException TooManyRequests(string code, string message) => new(429, code, message);
}
