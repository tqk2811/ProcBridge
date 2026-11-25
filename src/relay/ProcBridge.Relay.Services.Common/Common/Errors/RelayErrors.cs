namespace ProcBridge.Relay.Services.Common.Common.Errors;

/// <summary>Business error codes of the relay (data on the wire).</summary>
public static class RelayErrors
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string RegistrationDisabled = "REGISTRATION_DISABLED";
    public const string UserNameTaken = "USER_NAME_TAKEN";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string DeviceOffline = "DEVICE_OFFLINE";
    public const string ShareNotFound = "SHARE_NOT_FOUND";
    public const string ShareGrantNotFound = "SHARE_GRANT_NOT_FOUND";
    public const string ShareAccessDenied = "SHARE_ACCESS_DENIED";
    public const string TunnelSessionNotFound = "TUNNEL_SESSION_NOT_FOUND";
    public const string TunnelLaneInvalid = "TUNNEL_LANE_INVALID";
    public const string TunnelLaneBusy = "TUNNEL_LANE_BUSY";
}
