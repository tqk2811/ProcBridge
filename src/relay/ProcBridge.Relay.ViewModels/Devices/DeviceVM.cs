namespace ProcBridge.Relay.ViewModels.Devices;

/// <param name="IsOnline">The device currently holds a tunnel control channel to this relay.</param>
public sealed record DeviceVM(Guid Id, string Name, string? PublicKey, DateTime CreatedAt, bool IsRevoked, bool IsOnline);
