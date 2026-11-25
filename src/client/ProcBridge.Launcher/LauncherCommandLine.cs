using System.Text.Json;
using System.Text.Json.Serialization;
using ProcBridge.Broker.Models;
using ProcBridge.Launcher.Models;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Launcher;

/// <summary>Parses the Launcher command line into <see cref="LauncherOptions"/>.</summary>
public static class LauncherCommandLine
{
    public const string Usage =
        @"Usage: ProcBridge.Launcher --agent <host>[:port] --share <name> --map <virtual dir> <remote dir> [--map ...]" + "\n" +
        @"                           [--cache <dir>] [--arch x64|x86] [--spawn <ProcBridge.Spawn.exe>]" + "\n" +
        @"                           [--no-build-servers] [--cwd <dir>] [--agent-fingerprint <fp>] [--data <dir>]" + "\n" +
        @"                           [--plaintext] [--write-through] [--conflict keep|overwrite]" + "\n" +
        @"                           [--build-servers] [--no-plaintext] [--no-write-through]" + "\n" +
        @"                           [--relay <url>]" + "\n" +
        @"                           -- <program> [arguments]" + "\n" +
        @"       ProcBridge.Launcher --config <file.json> [options] [-- <program> [arguments]]" + "\n" +
        @"       ProcBridge.Launcher --pair --agent <host>[:port] [--data <dir>]" + "\n" +
        @"       ProcBridge.Launcher --pair --relay <url> [--code <relay pairing code>] [--data <dir>]" + "\n" +
        @"       ProcBridge.Launcher --list-paired [--data <dir>] | --revoke <fingerprint> [--data <dir>]" + "\n" +
        @"  <remote dir> is relative to the share, '/' separated ("""" for the share root)." + "\n" +
        @"  The Agent must be paired first (--pair); --plaintext talks to an unencrypted Agent (development only)." + "\n" +
        @"  --no-build-servers keeps MSBuild/Roslyn/dotnet build servers out of the run (they would outlive it)." + "\n" +
        @"  --write-through sends every write to machine B at once (default: on close or flush)." + "\n" +
        @"  --conflict: when machine B changed a file meanwhile, keep both (default) or let this machine win." + "\n" +
        @"  --relay reaches the Agent through a relay instead of --agent (which is then not needed); --agent-fingerprint" + "\n" +
        @"  picks the paired Agent, whose fingerprint is also its HostId on the relay (no relay account)." + "\n" +
        @"  --pair --relay finds the Agent by the 8-digit relay pairing code it shows (asked when --code is missing), then" + "\n" +
        @"  pairs as on the LAN: compare fingerprints and type the 6-digit code." + "\n" +
        @"  --config reads the options from a JSON file, e.g. {""agent"": ""10.0.0.5"", ""share"": ""src""," + "\n" +
        @"  ""maps"": [{""virtual"": ""V:\\Proj"", ""remote"": ""proj""}], ""noBuildServers"": true, ""command"": [""dotnet"", ""build""]}." + "\n" +
        @"  Options after --config override the file (--map adds a mapping); relative paths start at the file's folder." + "\n" +
        @"  --build-servers, --no-plaintext and --no-write-through undo a true ""noBuildServers"", ""plaintext"" or" + "\n" +
        @"  ""writeThrough"" of the file. A remote dir may not be absolute or contain '..'.";

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelt option must not be ignored silently.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Throws <see cref="ArgumentException"/> with a message for the user when the line is wrong.</summary>
    /// <param name="warning">Gets the warnings (plaintext from a file, several commands); null drops them.</param>
    public static LauncherOptions Parse(IReadOnlyList<string> args, Action<string>? warning = null)
    {
        args = ExpandConfigFiles(args, warning);
        string? host = null;
        int? port = null;
        string? share = null;
        string? cache = null;
        string? spawn = null;
        bool noBuildServers = false;
        bool plaintext = false;
        string? agentFingerprint = null;
        string? dataDirectory = null;
        string? workingDirectory = null;
        bool writeThrough = false;
        ConflictPolicy conflictPolicy = ConflictPolicy.KeepBoth;
        Uri? relayUrl = null;
        string architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var mappings = new List<VirtualMapping>();
        int index = 0;
        string Next(string option) => index + 1 < args.Count ? args[++index] : throw new ArgumentException($"Missing value for {option}.");

        for (; index < args.Count && args[index] != "--"; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--agent":
                    (string agentHost, int agentPort) = ParseAgent(Next(option));
                    host = agentHost;
                    port = agentPort;
                    break;
                case "--share":
                    share = Next(option);
                    break;
                case "--map":
                    string virtualRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Next(option)));
                    mappings.Add(new VirtualMapping(virtualRoot, CheckRemotePath(Next(option))));
                    break;
                case "--cache":
                    cache = Next(option);
                    break;
                case "--arch":
                    architecture = Next(option) is "x64" or "x86" ? args[index] : throw new ArgumentException("--arch expects x64 or x86.");
                    break;
                case "--spawn":
                    spawn = Next(option);
                    break;
                case "--no-build-servers":
                    noBuildServers = true;
                    break;
                case "--build-servers":
                    noBuildServers = false;
                    break;
                case "--plaintext":
                    plaintext = true;
                    break;
                case "--no-plaintext":
                    plaintext = false;
                    break;
                case "--agent-fingerprint":
                    agentFingerprint = Next(option);
                    break;
                case "--data":
                    dataDirectory = Next(option);
                    break;
                case "--cwd":
                    workingDirectory = Next(option);
                    break;
                case "--write-through":
                    writeThrough = true;
                    break;
                case "--no-write-through":
                    writeThrough = false;
                    break;
                case "--relay":
                    relayUrl = Uri.TryCreate(Next(option), UriKind.Absolute, out Uri? url) && url.Scheme is "https" or "http"
                        ? url
                        : throw new ArgumentException("--relay expects an http(s) URL.");
                    break;
                case "--conflict":
                    conflictPolicy = Next(option) switch
                    {
                        "keep" => ConflictPolicy.KeepBoth,
                        "overwrite" => ConflictPolicy.Overwrite,
                        _ => throw new ArgumentException("--conflict expects keep or overwrite."),
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument {option}.");
            }
        }
        LauncherRelay? relay = null;
        if (relayUrl is not null)
        {
            if (plaintext)
                throw new ArgumentException("--relay needs the encrypted transport (drop --plaintext).");
            if (agentFingerprint is null)
                throw new ArgumentException("--relay needs --agent-fingerprint (the paired Agent, its HostId on the relay).");
            relay = new LauncherRelay(relayUrl);
            host ??= relayUrl.Host;
        }
        if (host is null || share is null || mappings.Count == 0)
            throw new ArgumentException("--agent (or --relay), --share and at least one --map are required.");
        if (index + 1 >= args.Count)
            throw new ArgumentException("Missing '-- <program> [arguments]'.");

        return new LauncherOptions
        {
            AgentHost = host,
            AgentPort = port ?? Protocol.ProtocolInfo.DefaultPort,
            ShareName = share,
            Mappings = mappings,
            CacheRoot = cache,
            SpawnPath = spawn ?? Path.Combine(AppContext.BaseDirectory, $"ProcBridge.Spawn.{architecture}.exe"),
            DisableBuildServers = noBuildServers,
            AllowPlaintextTransport = plaintext,
            AgentFingerprint = agentFingerprint,
            DataDirectory = dataDirectory,
            WorkingDirectory = workingDirectory,
            WriteThrough = writeThrough,
            ConflictPolicy = conflictPolicy,
            Relay = relay,
            CommandLine = string.Join(' ', args.Skip(index + 1).Select(Quote)),
        };
    }

    /// <summary>
    /// Replaces each <c>--config &lt;file&gt;</c> (before <c>--</c>) by the options of the file, so options typed after it
    /// win; appends the file's command when the line has no <c>--</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The file cannot be read or has an unknown option.</exception>
    public static IReadOnlyList<string> ExpandConfigFiles(IReadOnlyList<string> args, Action<string>? warning = null)
    {
        var expanded = new List<string>();
        IReadOnlyList<string> command = [];
        int index = 0;
        for (; index < args.Count && args[index] != "--"; index++)
        {
            if (args[index] != "--config")
            {
                expanded.Add(args[index]);
                continue;
            }
            if (++index >= args.Count)
                throw new ArgumentException("Missing value for --config.");
            LauncherConfigFile config = ReadConfigFile(args[index]);
            expanded.AddRange(ToArguments(config, Path.GetDirectoryName(Path.GetFullPath(args[index]))!));
            if (config.Plaintext == true)
                warning?.Invoke($"Warning: --config file {args[index]} turns on the unencrypted transport (plaintext, development only).");
            if (config.Command.Count > 0)
            {
                if (command.Count > 0)
                    warning?.Invoke($"Warning: several --config files give a command; the one of {args[index]} is used.");
                command = config.Command;
            }
        }
        if (index < args.Count)
            expanded.AddRange(args.Skip(index));
        else if (command.Count > 0)
            expanded.AddRange(["--", .. command]);
        return expanded;
    }

    private static LauncherConfigFile ReadConfigFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<LauncherConfigFile>(File.ReadAllText(path), ConfigJson)
                ?? throw new ArgumentException($"--config file {path} is empty.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ArgumentException($"Cannot read --config file {path}: {e.Message}", e);
        }
    }

    private static List<string> ToArguments(LauncherConfigFile config, string baseDirectory)
    {
        string? Full(string? path) => path is null ? null : Path.GetFullPath(path, baseDirectory);
        var arguments = new List<string>();
        foreach ((string option, string? value) in new[]
        {
            ("--agent", config.Agent),
            ("--relay", config.Relay),
            ("--agent-fingerprint", config.AgentFingerprint),
            ("--share", config.Share),
            ("--cache", Full(config.Cache)),
            ("--arch", config.Arch),
            ("--spawn", Full(config.Spawn)),
            ("--cwd", Full(config.Cwd)),
            ("--data", Full(config.Data)),
            ("--conflict", config.Conflict),
        })
        {
            if (value is not null)
                arguments.AddRange([option, value]);
        }
        foreach (LauncherConfigMapping map in config.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.Virtual))
                throw new ArgumentException("Each \"maps\" item of --config needs \"virtual\".");
            arguments.AddRange(["--map", Full(map.Virtual)!, map.Remote]);
        }
        if (config.NoBuildServers is { } noBuildServers)
            arguments.Add(noBuildServers ? "--no-build-servers" : "--build-servers");
        if (config.Plaintext is { } plaintext)
            arguments.Add(plaintext ? "--plaintext" : "--no-plaintext");
        if (config.WriteThrough is { } writeThrough)
            arguments.Add(writeThrough ? "--write-through" : "--no-write-through");
        return arguments;
    }

    /// <summary>
    /// A share-relative folder: '/' or '\' separated, not rooted (<c>C:\x</c>, <c>\x</c>, drive-relative <c>V:x</c>) and
    /// without a <c>..</c> step that would leave the share.
    /// </summary>
    public static string CheckRemotePath(string remote)
    {
        if (Path.IsPathRooted(remote) || remote.Contains(':') || remote.Split('/', '\\').Contains(".."))
            throw new ArgumentException($"Remote dir '{remote}' must be relative to the share, without '..' or a drive.");
        return remote;
    }

    /// <summary>Splits <c>host[:port]</c>; the port defaults to <see cref="Protocol.ProtocolInfo.DefaultPort"/>.</summary>
    public static (string Host, int Port) ParseAgent(string agent)
    {
        int colon = agent.LastIndexOf(':');
        return colon > 0 && int.TryParse(agent[(colon + 1)..], out int port)
            ? (agent[..colon], port)
            : (agent, Protocol.ProtocolInfo.DefaultPort);
    }

    /// <summary>Quotes one argument for the usual (CommandLineToArgvW) parsing rules.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;
        var quoted = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes before a quote are doubled, plus one to escape the quote itself.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        // Backslashes before the closing quote are doubled.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
