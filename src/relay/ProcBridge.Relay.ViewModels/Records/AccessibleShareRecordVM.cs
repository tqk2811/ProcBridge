namespace ProcBridge.Relay.ViewModels.Records;

public sealed record AccessibleShareRecordVM(Guid ShareId, string Name, Guid HostDeviceId, string HostDeviceName, string? HostPublicKey);
