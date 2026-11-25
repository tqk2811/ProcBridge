namespace ProcBridge.Relay.ViewModels.Shares;

/// <summary>A share the calling device may open sessions to.</summary>
public sealed record AccessibleShareVM(Guid ShareId, string Name, Guid HostDeviceId, string HostDeviceName, string? HostPublicKey, bool IsHostOnline);
