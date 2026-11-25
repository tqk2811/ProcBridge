namespace ProcBridge.Launcher.Models;

/// <summary>Relay connection of a Launcher run (plan 8.2): relay URL, this machine's device token and the relay share id.</summary>
public sealed record LauncherRelay(Uri Url, string DeviceToken, Guid ShareId)
{
    /// <summary>Does not print the token.</summary>
    public override string ToString() => $"{Url} share {ShareId}";
}
