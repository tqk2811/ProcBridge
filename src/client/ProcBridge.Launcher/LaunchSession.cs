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
    private readonly Action<string>? _outputLine;

    /// <param name="outputLine">
    /// Null: the tree inherits this process' standard streams (console Launcher). Otherwise the tree gets hidden
    /// redirected streams and every stdout/stderr line is handed here (WPF Launcher), on a thread pool thread.
    /// </param>
    public LaunchSession(LauncherOptions options, Action<string>? outputLine = null)
    {
        _options = options;
        _outputLine = outputLine;
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
            Security = _options.Security,
            AllowPlaintextTransport = _options.AllowPlaintextTransport,
        });

        // Spawn passes everything after "--" to CreateProcess untouched.
        var startInfo = new ProcessStartInfo(_options.SpawnPath, $"--broker {SessionId} {Environment.ProcessId} -- {_options.CommandLine}")
        {
            UseShellExecute = false,
        };
        if (_options.WorkingDirectory is not null)
        {
            if (!Directory.Exists(_options.WorkingDirectory))
                throw new DirectoryNotFoundException($"Working directory {_options.WorkingDirectory} does not exist on this machine.");
            startInfo.WorkingDirectory = _options.WorkingDirectory;
        }
        if (_outputLine is not null)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
        }
        if (_options.DisableBuildServers)
        {
            foreach ((string name, string value) in BuildServerVariables)
                startInfo.Environment[name] = value;
        }
        using Process spawn = new() { StartInfo = startInfo };
        if (_outputLine is not null)
        {
            spawn.OutputDataReceived += (_, e) => { if (e.Data is not null) _outputLine(e.Data); };
            spawn.ErrorDataReceived += (_, e) => { if (e.Data is not null) _outputLine(e.Data); };
        }
        if (!spawn.Start())
            throw new InvalidOperationException("Cannot start ProcBridge.Spawn.");
        if (_outputLine is not null)
        {
            spawn.BeginOutputReadLine();
            spawn.BeginErrorReadLine();
        }
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
