namespace ProcBridge.Launcher.Models;

/// <summary>
/// Relay connection of a Launcher run (plan 8.2): only the relay URL. The session goes to the HostId of the paired Agent
/// (its key fingerprint); the relay has no accounts.
/// </summary>
public sealed record LauncherRelay(Uri Url)
{
    public override string ToString() => Url.ToString();
}
