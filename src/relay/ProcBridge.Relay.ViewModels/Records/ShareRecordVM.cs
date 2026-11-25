namespace ProcBridge.Relay.ViewModels.Records;

/// <param name="OwnerAccountId">Owner of the host device (joined column).</param>
public sealed record ShareRecordVM(Guid Id, Guid DeviceId, Guid OwnerAccountId, string Name, DateTime CreatedAt);
