namespace ProcBridge.Launcher.Wpf.Models;

/// <summary>A saved run: which machine B, which virtual folders, which command (the GUI form of the Launcher command line).</summary>
public sealed record LaunchProfile
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Fingerprint of the paired machine B.</summary>
    public string MachineFingerprint { get; init; } = string.Empty;

    /// <summary>Share of the Agent the run uses (<c>--share</c>).</summary>
    public string ShareName { get; init; } = string.Empty;

    /// <summary>
    /// Folder of the share on machine B. The Agent protocol addresses share content by relative path, so the folders
    /// picked with the Remote Explorer (absolute) are made relative to this one.
    /// </summary>
    public string ShareRoot { get; init; } = string.Empty;

    public IReadOnlyList<FolderMapping> Mappings { get; init; } = [];

    public string Program { get; init; } = string.Empty;

    /// <summary>Arguments as typed (already quoted by the user), appended after the quoted program.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Starting folder on machine A; empty to inherit.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary><c>--no-build-servers</c>.</summary>
    public bool DisableBuildServers { get; init; } = true;

    /// <summary><c>--write-through</c>: every write goes to machine B at once instead of at close or flush.</summary>
    public bool WriteThrough { get; init; }

    /// <summary><c>--conflict overwrite</c>: when machine B changed a file meanwhile, this machine wins; otherwise both are kept.</summary>
    public bool OverwriteOnConflict { get; init; }

    /// <summary>Reach machine B through a relay (<c>--relay</c>) instead of its LAN address.</summary>
    public bool UseRelay { get; init; }

    /// <summary>Relay URL (<c>--relay</c>). The session goes to the HostId of the paired machine; the relay has no accounts.</summary>
    public string RelayUrl { get; init; } = string.Empty;

    /// <summary>x64 or x86: which ProcBridge.Spawn starts the target.</summary>
    public string Architecture { get; init; } = "x64";
}
