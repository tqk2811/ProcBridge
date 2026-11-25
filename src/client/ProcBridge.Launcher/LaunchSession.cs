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
/// Broker once the whole process tree has exited.
/// </summary>
/// <remarks>
/// Spawn puts the target in a Job Object (kill on close, no breakaway: the hook keeps every child in it) and exits
/// only when every process of the job has exited, so waiting for Spawn waits for the tree; killing Spawn closes the
/// job and ends the tree.
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

    /// <summary>Variables set by <see cref="LauncherOptions.DisableBuildServers"/>.</summary>
    public static IReadOnlyDictionary<string, string> BuildServerVariables { get; } = new Dictionary<string, string>
    {
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        // MSBuild reads environment variables as properties: the Csc/Vbc tasks then compile in process.
        ["UseSharedCompilation"] = "false",
    };

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
        if (_options.DisableBuildServers)
        {
            foreach ((string name, string value) in BuildServerVariables)
                startInfo.Environment[name] = value;
        }
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
