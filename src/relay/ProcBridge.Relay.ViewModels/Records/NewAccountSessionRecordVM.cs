namespace ProcBridge.Relay.ViewModels.Records;

/// <param name="TokenHash">SHA-256 hex of the session token; the token itself is never stored.</param>
public sealed record NewAccountSessionRecordVM(Guid AccountId, string TokenHash, DateTime ExpiresAt);
