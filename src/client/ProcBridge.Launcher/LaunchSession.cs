using System.Diagnostics;
using System.Runtime.Versioning;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.Launcher.Models;

namespace ProcBridge.Launcher;

/// <summary>
/// One run (plan section 6): starts a Broker session with an unguessable id, starts the target through
/// ProcBridge.Spawn with that session (and this process as the pipe owner the hook must check), and stops the
/// Broker once the target has exited.
/// </summary>
/// <remarks>
/// Spawn waits for the target process only; children that outlive it lose the Broker. Waiting for the whole tree
/// (Job Object) comes with the process tree milestone (M4).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LaunchSession
{
    private readonly LauncherOptions _options;

    public LaunchSession(LauncherOptions options)
    {
        _options = options;
        SessionId = HookIpcProtocol.NewSessionId();
    }

    /// <summary>Names the Broker pipe (<c>ProcBridge-&lt;id&gt;</c>) and the default cache folder.</summary>
    public string SessionId { get; }

    /// <summary>Runs the target with inherited standard streams; returns its exit code (125 when Spawn fails).</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.SpawnPath))
            throw new FileNotFoundException("ProcBridge.Spawn not found.", _options.SpawnPath);

        await using BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = SessionId,
            AgentHost = _options.AgentHost,
            AgentPort = _options.AgentPort,
            ShareName = _options.ShareName,
            Mappings = _options.Mappings,
            CacheRoot = _options.CacheRoot,
        });

        // Spawn passes everything after "--" to CreateProcess untouched.
        var startInfo = new ProcessStartInfo(_options.SpawnPath, $"--broker {SessionId} {Environment.ProcessId} -- {_options.CommandLine}")
        {
            UseShellExecute = false,
        };
        using Process spawn = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start ProcBridge.Spawn.");
        try
        {
            await spawn.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            spawn.Kill(entireProcessTree: true);
            throw;
        }
        return spawn.ExitCode;
    }
}
