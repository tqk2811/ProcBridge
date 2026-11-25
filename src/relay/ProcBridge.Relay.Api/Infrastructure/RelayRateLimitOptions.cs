namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Configuration section <c>Relay:RateLimit</c>: fixed windows per client address on opening sessions, control
/// channels and lanes. Over the limit the relay answers 429. Counts of live sessions/lanes are capped separately
/// (<c>Relay:Tunnel</c>).
/// </summary>
public sealed class RelayRateLimitOptions
{
    public const string SectionName = "Relay:RateLimit";

    /// <summary>Policy on <c>POST api/tunnel/sessions</c>.</summary>
    public const string TunnelSessionPolicyName = "relay-tunnel-sessions";

    /// <summary>Policy on <c>GET /tunnel/control</c>.</summary>
    public const string ControlPolicyName = "relay-tunnel-control";

    /// <summary>Policy on <c>GET /tunnel</c> (lanes).</summary>
    public const string LanePolicyName = "relay-tunnel-lanes";

    /// <summary>Policy on <c>POST api/tunnel/pairing-sessions</c> (lookup of a pairing slot by invite code).</summary>
    public const string PairingPolicyName = "relay-tunnel-pairing";

    public int TunnelSessionPermitLimit { get; set; } = 60;

    /// <summary>Pairing lookups per window: low, because each one is a guess at an 8-digit invite code.</summary>
    public int PairingPermitLimit { get; set; } = 10;

    public int ControlPermitLimit { get; set; } = 20;

    /// <summary>Lanes per window: a pool opens several at once, so this is higher than the others.</summary>
    public int LanePermitLimit { get; set; } = 600;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
