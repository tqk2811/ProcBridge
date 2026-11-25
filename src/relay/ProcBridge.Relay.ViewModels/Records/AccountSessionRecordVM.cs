namespace ProcBridge.Relay.ViewModels.Records;

public sealed record AccountSessionRecordVM(Guid Id, Guid AccountId, DateTime ExpiresAt, DateTime? RevokedAt);
