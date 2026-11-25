using ProcBridge.Launcher.Wpf.Models;

namespace ProcBridge.Launcher.Wpf.Interfaces;

public interface IProfileStore
{
    /// <summary>Saved profiles; empty when nothing was saved yet.</summary>
    IReadOnlyList<LaunchProfile> Load();

    void Save(IReadOnlyList<LaunchProfile> profiles);
}
