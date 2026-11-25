using ProcBridge.Agent.Wpf.Core.Models;

namespace ProcBridge.Agent.Wpf.Core.Interfaces;

public interface IAgentSettingsStore
{
    /// <summary>Saved settings, or defaults when nothing was saved yet.</summary>
    /// <exception cref="InvalidDataException">The settings file is corrupted.</exception>
    AgentUiSettings Load();

    void Save(AgentUiSettings settings);
}
