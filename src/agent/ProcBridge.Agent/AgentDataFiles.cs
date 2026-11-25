namespace ProcBridge.Agent;

/// <summary>
/// Files the Agent keeps in its data folder, shared by the console host and the WPF app so both see the same identity,
/// paired machines and Explorer profiles.
/// </summary>
public static class AgentDataFiles
{
    public const string IdentityKey = "identity.key";

    public const string PairedMachines = "paired-machines.bin";

    public const string ExplorerPolicies = "explorer-policies.bin";

    /// <summary><c>%LOCALAPPDATA%\ProcBridge\agent</c>.</summary>
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "agent");
}
