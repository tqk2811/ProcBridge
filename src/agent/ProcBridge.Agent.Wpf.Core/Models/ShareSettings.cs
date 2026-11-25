namespace ProcBridge.Agent.Wpf.Core.Models;

/// <summary>One shared folder as saved by the app.</summary>
public sealed record ShareSettings
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    /// <summary>
    /// Owner allows machine A to write into this share. Saved only: the Agent serves shares read only until the write
    /// path (M3) lands on the server side (see backlog).
    /// </summary>
    public bool AllowWrite { get; init; }
}
