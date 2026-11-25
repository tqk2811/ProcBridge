using ProcBridge.Protocol;

namespace ProcBridge.Agent.Wpf.Core.Models;

/// <summary>
/// Settings of the Agent app (plain JSON; keys and trust stay in the DPAPI stores). The relay needs only its URL.
/// </summary>
public sealed class AgentUiSettings
{
    public int Port { get; set; } = ProtocolInfo.DefaultPort;

    public string BindAddress { get; set; } = "0.0.0.0";

    public List<ShareSettings> Shares { get; set; } = [];

    /// <summary>Relay URL (plan 8.2); empty serves the LAN only.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>Start the server as soon as the app opens.</summary>
    public bool StartServerOnLaunch { get; set; }

    /// <summary>Closing the window hides it to the notification area instead of quitting.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Last time each paired machine A (by fingerprint) was seen connected.</summary>
    public Dictionary<string, DateTime> LastSeenUtc { get; set; } = new(StringComparer.Ordinal);
}
