using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class ServiceEntry
{
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public string? DisplayName { get; init; }

    /// <summary><see cref="System.ServiceProcess.ServiceControllerStatus"/> name, e.g. Running.</summary>
    [Key(2)]
    public string? Status { get; init; }

    /// <summary><see cref="System.ServiceProcess.ServiceStartMode"/> name, e.g. Automatic.</summary>
    [Key(3)]
    public string? StartType { get; init; }

    [Key(4)]
    public string? ServiceType { get; init; }
}
