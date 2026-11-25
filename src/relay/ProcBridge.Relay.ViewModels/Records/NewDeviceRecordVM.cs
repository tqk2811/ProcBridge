namespace ProcBridge.Relay.ViewModels.Records;

/// <param name="TokenHash">SHA-256 hex of the device token; the token itself is never stored.</param>
public sealed record NewDeviceRecordVM(Guid OwnerAccountId, string Name, string? PublicKey, string TokenHash);
