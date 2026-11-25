namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Configuration section <c>Relay:RateLimit</c>: how many sign-in and registration attempts one client address may
/// make per window (password guessing, mass registration). Over the limit the relay answers 429.
/// </summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "Relay:RateLimit";

    /// <summary>Name of the rate limiter policy on <c>login</c> and <c>register</c>.</summary>
    public const string PolicyName = "relay-auth";

    public int AuthPermitLimit { get; set; } = 10;

    public TimeSpan AuthWindow { get; set; } = TimeSpan.FromMinutes(1);
}
