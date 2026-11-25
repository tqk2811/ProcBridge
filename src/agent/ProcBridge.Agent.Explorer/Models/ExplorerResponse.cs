using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Common part of every Explorer response. Derived classes use keys from 2 upward.</summary>
public abstract class ExplorerResponse
{
    [Key(0)]
    public ExplorerStatus Status { get; set; } = ExplorerStatus.Ok;

    /// <summary>Human readable reason when <see cref="Status"/> is not Ok.</summary>
    [Key(1)]
    public string? Error { get; set; }
}
