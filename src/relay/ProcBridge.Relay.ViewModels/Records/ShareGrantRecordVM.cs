namespace ProcBridge.Relay.ViewModels.Records;

public sealed record ShareGrantRecordVM(Guid ShareId, Guid GranteeDeviceId, DateTime CreatedAt);
