namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Configuration section <c>Relay:ForwardedHeaders</c>: the reverse proxies whose <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c> the relay believes. Both lists empty (the default) means the relay trusts no such header:
/// anyone could otherwise pick their own address and walk around the per-address rate limits.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "Relay:ForwardedHeaders";

    /// <summary>Addresses of trusted proxies (the peer the relay sees), e.g. <c>10.0.0.5</c>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Trusted proxy networks in CIDR form, e.g. <c>10.0.0.0/24</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    public bool IsEmpty => KnownProxies.Length == 0 && KnownNetworks.Length == 0;
}
