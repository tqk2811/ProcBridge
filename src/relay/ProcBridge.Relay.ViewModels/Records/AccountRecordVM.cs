namespace ProcBridge.Relay.ViewModels.Records;

public sealed record AccountRecordVM(Guid Id, string UserName, string PasswordHash, DateTime CreatedAt);
