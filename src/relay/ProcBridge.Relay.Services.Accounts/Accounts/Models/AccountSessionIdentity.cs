namespace ProcBridge.Relay.Services.Accounts.Models;

/// <summary>Who a valid account session token belongs to.</summary>
public sealed record AccountSessionIdentity(Guid AccountId, Guid SessionId);
