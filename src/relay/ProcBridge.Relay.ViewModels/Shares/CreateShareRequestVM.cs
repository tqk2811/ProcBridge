namespace ProcBridge.Relay.ViewModels.Shares;

public sealed record CreateShareRequestVM
{
    /// <summary>Host device (machine B) that serves the share; must belong to the caller.</summary>
    public required Guid DeviceId { get; init; }
    public required string Name { get; init; }
}
