using System.Security.Claims;

using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Shared.Auth;

namespace ProcBridge.Relay.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetAccountId(this ClaimsPrincipal user) => GetGuid(user, RelayAuth.AccountIdClaim);

    public static Guid GetSessionId(this ClaimsPrincipal user) => GetGuid(user, RelayAuth.SessionIdClaim);

    public static Guid GetDeviceId(this ClaimsPrincipal user) => GetGuid(user, RelayAuth.DeviceIdClaim);

    private static Guid GetGuid(ClaimsPrincipal user, string type)
        => Guid.TryParse(user.FindFirstValue(type), out var id)
            ? id
            : throw ApiErrorException.Unauthorized("INVALID_TOKEN", "Token không hợp lệ cho thao tác này.");
}
