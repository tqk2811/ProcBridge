namespace ProcBridge.Relay.Services.Common.Common.Errors;

/// <summary>Business error codes of the relay (data on the wire).</summary>
public static class RelayErrors
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string HostOffline = "HOST_OFFLINE";
    public const string HostIdNotProven = "HOST_ID_NOT_PROVEN";
    public const string TooManySessions = "TOO_MANY_SESSIONS";
    public const string TooManyLanes = "TOO_MANY_LANES";
    public const string TooManyControlChannels = "TOO_MANY_CONTROL_CHANNELS";
    public const string TunnelSessionNotFound = "TUNNEL_SESSION_NOT_FOUND";
    public const string TunnelLaneInvalid = "TUNNEL_LANE_INVALID";
    public const string TunnelLaneBusy = "TUNNEL_LANE_BUSY";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
}
