namespace ProcBridge.Relay.Services.Common.Presence.Interfaces;

/// <summary>
/// Cuts live tunnel connections after a revocation (device revoked, grant revoked, share deleted), so the change takes
/// effect on open lanes and not only on the next session. Implemented by the Tunnel feature.
/// </summary>
public interface ITunnelTerminator
{
    /// <summary>Closes the control channel of the device and every lane of the sessions it is an end of.</summary>
    void DisconnectDevice(Guid deviceId);

    /// <summary>Closes the lanes of the sessions to <paramref name="shareId"/>; only those of <paramref name="clientDeviceId"/> when given.</summary>
    void DisconnectShare(Guid shareId, Guid? clientDeviceId = null);
}
