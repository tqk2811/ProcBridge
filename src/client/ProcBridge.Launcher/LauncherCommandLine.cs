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
        @"                           [--relay <url>]" + "\n" +
        @"                           -- <program> [arguments]" + "\n" +
        @"       ProcBridge.Launcher --pair --agent <host>[:port] [--data <dir>]" + "\n" +
        @"       ProcBridge.Launcher --list-paired [--data <dir>] | --revoke <fingerprint> [--data <dir>]" + "\n" +
        @"  <remote dir> is relative to the share, '/' separated ("""" for the share root)." + "\n" +
        @"  The Agent must be paired first (--pair); --plaintext talks to an unencrypted Agent (development only)." + "\n" +
        @"  --no-build-servers keeps MSBuild/Roslyn/dotnet build servers out of the run (they would outlive it)." + "\n" +
        @"  --write-through sends every write to machine B at once (default: on close or flush)." + "\n" +
        @"  --conflict: when machine B changed a file meanwhile, keep both (default) or let this machine win." + "\n" +
        @"  --relay reaches the Agent through a relay instead of --agent (which is then not needed); --agent-fingerprint" + "\n" +
        @"  picks the paired Agent, whose fingerprint is also its HostId on the relay (no relay account).";

    /// <summary>Throws <see cref="ArgumentException"/> with a message for the user when the line is wrong.</summary>
    public static LauncherOptions Parse(IReadOnlyList<string> args)
    {
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
                    mappings.Add(new VirtualMapping(virtualRoot, Next(option)));
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
                case "--plaintext":
                    plaintext = true;
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
