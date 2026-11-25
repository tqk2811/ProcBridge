namespace ProcBridge.Relay.Cli;

/// <summary>What <c>login</c> remembers between runs.</summary>
public sealed class CliConfig
{
    public string? Url { get; set; }
    public string? Token { get; set; }
}
