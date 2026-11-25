namespace ProcBridge.Relay.Services.Devices.Models;

/// <summary>Who a valid device token belongs to.</summary>
public sealed record DeviceIdentity(Guid DeviceId, Guid OwnerAccountId);
