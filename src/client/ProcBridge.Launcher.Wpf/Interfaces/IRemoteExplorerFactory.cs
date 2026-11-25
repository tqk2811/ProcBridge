using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Interfaces;

public interface IRemoteExplorerFactory
{
    /// <summary>Opens a Remote Explorer to <paramref name="machine"/> (lazy: connects on the first call); the caller disposes it.</summary>
    IRemoteExplorer Create(PairedMachine machine);
}
