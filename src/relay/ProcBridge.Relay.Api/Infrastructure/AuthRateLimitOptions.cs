namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Configuration section <c>Relay:RateLimit</c>: how many sign-in and registration attempts one client address may
/// make per window (password guessing, mass registration), and how many tunnel sessions one device may open.
/// Over the limit the relay answers 429.
/// </summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "Relay:RateLimit";

    /// <summary>Name of the rate limiter policy on <c>login</c> and <c>register</c>.</summary>
    public const string PolicyName = "relay-auth";

    /// <summary>Name of the rate limiter policy on <c>POST api/tunnel/sessions</c> (partitioned by device).</summary>
    public const string TunnelSessionPolicyName = "relay-tunnel-sessions";

    public int AuthPermitLimit { get; set; } = 10;

    public TimeSpan AuthWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Sign-in attempts per user name per <see cref="AuthWindow"/>, counted on top of the per-address limit: guessing one
    /// account's password from many addresses (or through a proxy that hides them) still runs into this.
    /// </summary>
    public int LoginUserNamePermitLimit { get; set; } = 10;

    /// <summary>Tunnel sessions one device may open per <see cref="TunnelSessionWindow"/> (the host keeps a ticket per session).</summary>
    public int TunnelSessionPermitLimit { get; set; } = 60;

    public TimeSpan TunnelSessionWindow { get; set; } = TimeSpan.FromMinutes(1);
}
