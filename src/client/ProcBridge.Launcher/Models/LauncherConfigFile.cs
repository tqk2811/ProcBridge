namespace ProcBridge.Launcher.Models;

/// <summary>
/// JSON form of the run options (<c>--config &lt;file&gt;</c>): each property is the command line option of the same name.
/// Options typed on the command line after <c>--config</c> override the file; <c>--map</c> adds to <see cref="Maps"/>.
/// Relative paths are taken from the file's folder.
/// </summary>
public sealed record LauncherConfigFile
{
    /// <summary><c>--agent host[:port]</c>.</summary>
    public string? Agent { get; init; }

    /// <summary><c>--relay url</c>.</summary>
    public string? Relay { get; init; }

    /// <summary><c>--agent-fingerprint</c>.</summary>
    public string? AgentFingerprint { get; init; }

    /// <summary><c>--share</c>.</summary>
    public string? Share { get; init; }

    /// <summary>One <c>--map</c> each.</summary>
    public IReadOnlyList<LauncherConfigMapping> Maps { get; init; } = [];

    public string? Cache { get; init; }

    /// <summary>x64 or x86 (<c>--arch</c>).</summary>
    public string? Arch { get; init; }

    public string? Spawn { get; init; }

    public bool? NoBuildServers { get; init; }

    public string? Cwd { get; init; }

    public string? Data { get; init; }

    public bool? Plaintext { get; init; }

    public bool? WriteThrough { get; init; }

    /// <summary>keep or overwrite (<c>--conflict</c>).</summary>
    public string? Conflict { get; init; }

    /// <summary>Program and arguments, one item each (what follows <c>--</c>); used when the command line has no <c>--</c>.</summary>
    public IReadOnlyList<string> Command { get; init; } = [];
}

/// <summary>One <c>--map &lt;virtual dir&gt; &lt;remote dir&gt;</c> of <see cref="LauncherConfigFile"/>.</summary>
public sealed record LauncherConfigMapping
{
    /// <summary>Folder on this machine (relative to the file's folder when not absolute).</summary>
    public string Virtual { get; init; } = string.Empty;

    /// <summary>Folder relative to the share, '/' separated; empty for the share root.</summary>
    public string Remote { get; init; } = string.Empty;
}
