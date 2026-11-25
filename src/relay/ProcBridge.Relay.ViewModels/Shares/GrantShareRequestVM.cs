namespace ProcBridge.Relay.ViewModels.Shares;

public sealed record GrantShareRequestVM
{
    /// <summary>Client device (machine A) allowed to open tunnel sessions to the share. May belong to any account.</summary>
    public required Guid DeviceId { get; init; }
}
