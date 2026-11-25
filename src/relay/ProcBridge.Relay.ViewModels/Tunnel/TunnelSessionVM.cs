namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <param name="Ticket">Short-lived secret that authorizes the client lanes of this session (<c>Authorization: Bearer</c>).</param>
/// <param name="ExpiresAt">New lanes must be opened before this time (UTC); open lanes are not cut at expiry.</param>
public sealed record TunnelSessionVM(Guid SessionId, string Ticket, DateTime ExpiresAt);
