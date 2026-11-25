namespace ProcBridge.Relay.Shared.Auth;

/// <summary>Authentication scheme, claim types, policy names and token prefixes of the relay.</summary>
public static class RelayAuth
{
    public const string Scheme = "RelayBearer";

    public const string AccountIdClaim = "procbridge:account";
    public const string DeviceIdClaim = "procbridge:device";
    public const string SessionIdClaim = "procbridge:session";

    /// <summary>Caller signed in as a person (account session token).</summary>
    public const string AccountPolicy = "RelayAccount";

    /// <summary>Caller is a registered machine (device token).</summary>
    public const string DevicePolicy = "RelayDevice";

    /// <summary>Prefix of account session tokens, so one bearer header can carry either kind.</summary>
    public const string SessionTokenPrefix = "pbs_";

    /// <summary>Prefix of device tokens.</summary>
    public const string DeviceTokenPrefix = "pbd_";

    /// <summary>
    /// Prefix of tunnel lane tickets. A lane carries its ticket as <c>Authorization: Bearer pbt_...</c>; in the URL it
    /// would end up in proxy and access logs.
    /// </summary>
    public const string TicketPrefix = "pbt_";
}
