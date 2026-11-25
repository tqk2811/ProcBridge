namespace ProcBridge.Relay.ViewModels.Records;

public sealed record DeviceRecordVM(Guid Id, Guid OwnerAccountId, string Name, string? PublicKey, DateTime CreatedAt, DateTime? RevokedAt);
