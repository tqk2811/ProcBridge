namespace ProcBridge.Launcher.Wpf.Models;

/// <summary>One virtual folder of a profile: a path on machine A served from a folder of machine B.</summary>
public sealed record FolderMapping
{
    /// <summary>Absolute path on machine A seen by the process tree.</summary>
    public string VirtualRoot { get; init; } = string.Empty;

    /// <summary>Absolute path on machine B, picked with the Remote Explorer; must lie inside the profile's share root.</summary>
    public string RemotePath { get; init; } = string.Empty;
}
