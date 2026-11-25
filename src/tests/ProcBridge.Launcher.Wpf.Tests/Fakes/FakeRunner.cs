using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Interfaces;

namespace ProcBridge.Launcher.Wpf.Tests.Fakes;

/// <summary>Records the options, writes scripted lines, then exits with <see cref="ExitCode"/> or waits to be cancelled.</summary>
internal sealed class FakeRunner : ILaunchRunner
{
    public LauncherOptions? Options { get; private set; }

    public string[] Lines { get; set; } = [];

    public int ExitCode { get; set; }

    /// <summary>When set, the run lasts until cancelled (as a real tree would).</summary>
    public bool WaitForCancel { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool WasCancelled { get; private set; }

    /// <summary>Thrown by the run instead of an exit code (a failed preflight of LaunchSession).</summary>
    public Exception? Failure { get; set; }

    public async Task<int> RunAsync(LauncherOptions options, Action<string> outputLine, CancellationToken cancellationToken)
    {
        Options = options;
        if (Failure is not null)
            throw Failure;
        foreach (string line in Lines)
            outputLine(line);
        Started.TrySetResult();
        if (WaitForCancel)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
        return ExitCode;
    }
}
