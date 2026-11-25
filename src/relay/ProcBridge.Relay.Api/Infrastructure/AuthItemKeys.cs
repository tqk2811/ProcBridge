namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>Keys of <c>HttpContext.Items</c> that authentication leaves for the endpoint.</summary>
public static class AuthItemKeys
{
    /// <summary>
    /// <c>ITunnelHub.RevocationEpoch</c> read BEFORE the device token was checked in the database: the control channel
    /// compares it with the epoch after registering, so a revocation between those two points is noticed.
    /// </summary>
    public const string RevocationEpoch = "procbridge:revocation-epoch";
}
