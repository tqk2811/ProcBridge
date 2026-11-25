using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;

namespace ProcBridge.Launcher.Wpf.Tests.Fakes;

internal sealed class MemoryProfileStore : IProfileStore
{
    public List<LaunchProfile> Saved { get; } = [];

    public IReadOnlyList<LaunchProfile> Load() => Saved.ToArray();

    public void Save(IReadOnlyList<LaunchProfile> profiles)
    {
        Saved.Clear();
        Saved.AddRange(profiles);
    }
}
