using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>
/// One registry value. Exactly one data field is used, chosen by <see cref="Type"/>:
/// String/ExpandString use <see cref="Text"/>, DWord/QWord use <see cref="Number"/>,
/// MultiString uses <see cref="Strings"/>, Binary/None/Unknown use <see cref="Bytes"/>.
/// </summary>
[MessagePackObject]
public sealed class RegistryValue
{
    /// <summary>Value name, empty string for the default value.</summary>
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public required RegistryValueType Type { get; init; }

    [Key(2)]
    public string? Text { get; init; }

    [Key(3)]
    public long? Number { get; init; }

    [Key(4)]
    public IReadOnlyList<string>? Strings { get; init; }

    [Key(5)]
    public byte[]? Bytes { get; init; }
}
