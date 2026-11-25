namespace ProcBridge.Relay.Services.Common.Presence.Interfaces;

/// <summary>
/// Which devices hold a live tunnel control channel. Implemented by the Tunnel feature, read by Devices and Shares
/// (kept here so features do not reference each other).
/// </summary>
public interface IDevicePresence
{
    bool IsOnline(Guid deviceId);
}
