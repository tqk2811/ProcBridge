using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using ProcBridge.Relay.Services.Accounts.Interfaces;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Devices.Interfaces;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.Shared.Common;
using ProcBridge.Relay.Shared.Json;

namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// One bearer scheme for two kinds of opaque token, told apart by prefix: account session tokens give the account
/// claims, device tokens give the device claim only (a device token never acts as its owner account).
/// </summary>
public sealed class RelayBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAccountService _accounts;
    private readonly IDeviceService _devices;

    public RelayBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAccountService accounts,
        IDeviceService devices) : base(options, logger, encoder)
    {
        _accounts = accounts;
        _devices = devices;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();

        List<Claim> claims;
        if (token.StartsWith(RelayAuth.SessionTokenPrefix, StringComparison.Ordinal))
        {
            var identity = await _accounts.AuthenticateAsync(token, Context.RequestAborted);
            if (identity is null)
                return AuthenticateResult.Fail("Invalid session token.");
            claims = [new(RelayAuth.AccountIdClaim, identity.AccountId.ToString()), new(RelayAuth.SessionIdClaim, identity.SessionId.ToString())];
        }
        else if (token.StartsWith(RelayAuth.DeviceTokenPrefix, StringComparison.Ordinal))
        {
            var identity = await _devices.AuthenticateAsync(token, Context.RequestAborted);
            if (identity is null)
                return AuthenticateResult.Fail("Invalid device token.");
            claims = [new(RelayAuth.DeviceIdClaim, identity.DeviceId.ToString())];
        }
        else
        {
            return AuthenticateResult.Fail("Unknown token kind.");
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, RelayAuth.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, RelayAuth.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => WriteErrorAsync(StatusCodes.Status401Unauthorized, RelayErrors.InvalidToken, "Thiếu hoặc sai token.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => WriteErrorAsync(StatusCodes.Status403Forbidden, "FORBIDDEN", "Token này không dùng được cho thao tác đó.");

    private Task WriteErrorAsync(int status, string code, string message)
    {
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        var body = new ApiErrorResponse
        {
            RequestId = Context.Items[RequestIdKeys.ItemKey]?.ToString() ?? string.Empty,
            Error = new ApiError { Code = code, Message = message },
        };
        return Response.WriteAsync(JsonConvert.SerializeObject(body, RelayJsonSettings.Create()));
    }
}
