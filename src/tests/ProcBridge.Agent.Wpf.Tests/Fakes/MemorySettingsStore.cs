using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Models;

namespace ProcBridge.Agent.Wpf.Tests.Fakes;

internal sealed class MemorySettingsStore : IAgentSettingsStore
{
    public AgentUiSettings Settings { get; set; } = new();

    public int SaveCalls { get; private set; }

    public AgentUiSettings Load() => Settings;

    public void Save(AgentUiSettings settings)
    {
        SaveCalls++;
        Settings = settings;
    }
}
