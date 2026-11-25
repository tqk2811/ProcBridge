using System.Runtime.Versioning;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Launcher;

/// <summary>
/// Data folder of machine A (<c>--data</c>, default <c>%LOCALAPPDATA%\ProcBridge\launcher</c>): the DPAPI protected
/// identity key and the paired machine list, shared by the console Launcher and the WPF Launcher.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LauncherDataStore
{
    /// <param name="dataDirectory">Null for the default folder.</param>
    public LauncherDataStore(string? dataDirectory)
    {
        Directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "launcher");
    }

    public string Directory { get; }

    /// <summary>Loads the identity key, creating it on first use; the caller owns (disposes) it.</summary>
    public IdentityKey OpenIdentity()
        => new IdentityKeyStore(Path.Combine(Directory, "identity.key"), new DpapiKeyProtector()).LoadOrCreate();

    public PairedMachineStore OpenPairedMachines()
        => new(Path.Combine(Directory, "paired-machines.bin"), new DpapiKeyProtector());
}
