namespace ProcBridge.Relay.Shared.Auth;

/// <summary>
/// Secrets of the relay. The relay has no accounts, devices or tokens: a host proves its identity key with a Noise
/// handshake on the control channel, and a lane is authorized by its session ticket.
/// </summary>
public static class RelayAuth
{
    /// <summary>
    /// Prefix of tunnel lane tickets. A lane carries its ticket as <c>Authorization: Bearer pbt_...</c>; in the URL it
    /// would end up in proxy and access logs.
    /// </summary>
    public const string TicketPrefix = "pbt_";
}
