using System.Diagnostics;
using System.Runtime.Versioning;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Transport.Relay.Models;
using ProcBridge.HookIpc;
using ProcBridge.Launcher.Enums;
using ProcBridge.Launcher.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;

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
    /// <exception cref="LaunchFailedException">
    /// The Agent cannot be reached, has no such share, or the working directory does not exist: checked before the target
    /// starts, so the user gets that reason instead of Spawn's "inject failed".
    /// </exception>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.SpawnPath))
            throw new FileNotFoundException("ProcBridge.Spawn not found.", _options.SpawnPath);
        if (_options.WorkingDirectory is not null && VirtualWorkingDirectory is null && !Directory.Exists(_options.WorkingDirectory))
        {
            throw new LaunchFailedException(LaunchFailureReason.WorkingDirectoryMissing, _options.WorkingDirectory, string.Empty,
                $"Working directory {_options.WorkingDirectory} does not exist on this machine.");
        }

        BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = SessionId,
            AgentHost = _options.AgentHost,
            AgentPort = _options.AgentPort,
            ShareName = _options.ShareName,
            Mappings = _options.Mappings,
            CacheRoot = _options.CacheRoot,
            Security = _options.Security,
            AllowPlaintextTransport = _options.AllowPlaintextTransport,
            WriteThrough = _options.WriteThrough,
            ConflictPolicy = _options.ConflictPolicy,
            Relay = _options.Relay is { } relay
                ? new BrokerRelayOptions { Endpoint = new RelayEndpoint(relay.Url) }
                : null,
        });
        try
        {
            await CheckAgentAsync(broker, cancellationToken).ConfigureAwait(false);
            await CheckVirtualWorkingDirectoryAsync(broker, cancellationToken).ConfigureAwait(false);
            return await RunSpawnAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The last write-backs run while the Broker stops, so conflicts are complete only after that.
            await broker.DisposeAsync().ConfigureAwait(false);
            ReportConflicts(broker.WriteBack.Conflicts);
        }
    }

    /// <summary>
    /// Share-relative folder of <see cref="LauncherOptions.WorkingDirectory"/> when it lies in a virtual folder (it exists
    /// on machine B only, so Spawn hands it to the hook instead of starting there); null otherwise.
    /// </summary>
    public string? VirtualWorkingDirectory => _options.WorkingDirectory is { } directory ? ToRemotePath(_options.Mappings, directory) : null;

    /// <summary>
    /// Share-relative path of <paramref name="localPath"/> under the innermost mapping holding it ('/' separated), or null
    /// when no mapping holds it.
    /// </summary>
    public static string? ToRemotePath(IReadOnlyList<VirtualMapping> mappings, string localPath)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
        VirtualMapping? holder = null;
        string rest = string.Empty;
        foreach (VirtualMapping mapping in mappings)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.VirtualRoot));
            string? candidate = string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : full.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase) ? full[(root.Length + 1)..] : null;
            if (candidate is not null && (holder is null || root.Length > holder.VirtualRoot.Length))
            {
                holder = mapping with { VirtualRoot = root };
                rest = candidate;
            }
        }
        if (holder is null)
            return null;
        string remote = holder.RemotePath.Trim('/');
        rest = rest.Replace('\\', '/');
        return remote.Length == 0 ? rest : rest.Length == 0 ? remote : remote + "/" + rest;
    }

    /// <summary>A working directory in a virtual folder must exist on machine B: checked here rather than by the hook, whose failure reads "inject failed".</summary>
    private async Task CheckVirtualWorkingDirectoryAsync(BrokerHost broker, CancellationToken cancellationToken)
    {
        if (VirtualWorkingDirectory is not { } remote)
            return;
        bool exists;
        try
        {
            exists = (await broker.Client.StatAsync(remote, cancellationToken).ConfigureAwait(false)).IsDirectory;
        }
        catch (ProtocolStatusException e) when (!e.IsConnectionFailure && e.Status is NtStatus.ObjectNameNotFound or NtStatus.ObjectPathNotFound)
        {
            exists = false;
        }
        if (!exists)
        {
            throw new LaunchFailedException(LaunchFailureReason.WorkingDirectoryMissing, _options.WorkingDirectory!, string.Empty,
                $"Working directory {_options.WorkingDirectory} does not exist on machine B ('{remote}' in share '{_options.ShareName}').");
        }
    }

    /// <summary>Where the Agent is reached, for messages.</summary>
    private string AgentDescription => _options.Relay is { } relay
        ? $"relay {relay.Url} (Agent {_options.AgentFingerprint})"
        : $"{_options.AgentHost}:{_options.AgentPort}";

    /// <summary>
    /// Opens a connection of the Broker's pool (Hello with the share) before the target starts. Without it the hook of the
    /// target cannot initialize and Spawn only reports "inject failed".
    /// </summary>
    private async Task CheckAgentAsync(BrokerHost broker, CancellationToken cancellationToken)
    {
        try
        {
            await broker.Client.Pool.GetServerHelloAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolStatusException e) when (e.Status == NtStatus.BadNetworkName && !e.IsConnectionFailure)
        {
            throw new LaunchFailedException(LaunchFailureReason.ShareUnknown, _options.ShareName, e.Message,
                $"The Agent at {AgentDescription} has no share '{_options.ShareName}'.", e);
        }
        catch (Exception e) when (e is not OutOfMemoryException && !(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new LaunchFailedException(LaunchFailureReason.AgentUnreachable, AgentDescription, e.Message,
                $"Cannot reach the Agent at {AgentDescription}: {e.Message} Check that the Agent runs on machine B, the address (or relay) and that this machine is still paired.", e);
        }
    }

    /// <summary>Warns about files machine B changed meanwhile: machine A's version was kept beside it (or overwrote it).</summary>
    private void ReportConflicts(IReadOnlyList<WriteConflict> conflicts)
    {
        foreach (WriteConflict conflict in conflicts)
        {
            string line = $"ProcBridge: conflict on '{conflict.RemotePath}': machine B changed it meanwhile; this machine's version was saved as '{conflict.ConflictPath}'.";
            if (_outputLine is not null)
                _outputLine(line);
            else
                Console.Error.WriteLine(line);
        }
    }

    private async Task<int> RunSpawnAsync(CancellationToken cancellationToken)
    {

        // A virtual working directory does not exist for Spawn (not hooked): the hook of the target sets it (--cwd).
        string cwd = string.Empty;
        if (_options.WorkingDirectory is not null && VirtualWorkingDirectory is not null)
            cwd = $"--cwd {LauncherCommandLine.Quote(Path.GetFullPath(_options.WorkingDirectory))} ";
        // Spawn passes everything after "--" to CreateProcess untouched.
        var startInfo = new ProcessStartInfo(_options.SpawnPath, $"{cwd}--broker {SessionId} {Environment.ProcessId} -- {_options.CommandLine}")
        {
            UseShellExecute = false,
        };
        if (_options.WorkingDirectory is not null && cwd.Length == 0)
            startInfo.WorkingDirectory = _options.WorkingDirectory;
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
