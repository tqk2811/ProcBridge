namespace ProcBridge.Relay.ViewModels.Devices;

/// <param name="Token">Device token (bearer). Shown once; the relay only keeps its hash.</param>
public sealed record DeviceRegisteredVM(DeviceVM Device, string Token);
