using ProcBridge.Launcher.Models;

namespace ProcBridge.Launcher.Wpf.Interfaces;

/// <summary>Runs one <see cref="LauncherOptions"/> through the shared <c>LaunchSession</c>.</summary>
public interface ILaunchRunner
{
    /// <summary>
    /// Returns the exit code of the target. Cancelling kills the whole tree and throws
    /// <see cref="OperationCanceledException"/>. <paramref name="outputLine"/> may be called from any thread.
    /// </summary>
    Task<int> RunAsync(LauncherOptions options, Action<string> outputLine, CancellationToken cancellationToken);
}
