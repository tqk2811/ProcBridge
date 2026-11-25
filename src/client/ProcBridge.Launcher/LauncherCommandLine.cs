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
        @"                           [--no-build-servers] [--write-through] [--conflict keep|overwrite] -- <program> [arguments]" + "\n" +
        @"  <remote dir> is relative to the share, '/' separated ("""" for the share root)." + "\n" +
        @"  --no-build-servers keeps MSBuild/Roslyn/dotnet build servers out of the run (they would outlive it)." + "\n" +
        @"  --write-through sends every write to machine B at once (default: on close or flush)." + "\n" +
        @"  --conflict: when machine B changed a file meanwhile, keep both (default) or let this machine win.";

    /// <summary>Throws <see cref="ArgumentException"/> with a message for the user when the line is wrong.</summary>
    public static LauncherOptions Parse(IReadOnlyList<string> args)
    {
        string? host = null;
        int? port = null;
        string? share = null;
        string? cache = null;
        string? spawn = null;
        bool noBuildServers = false;
        bool writeThrough = false;
        ConflictPolicy conflictPolicy = ConflictPolicy.KeepBoth;
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
                    string agent = Next(option);
                    int colon = agent.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(agent[(colon + 1)..], out int parsedPort))
                    {
                        host = agent[..colon];
                        port = parsedPort;
                    }
                    else
                    {
                        host = agent;
                    }
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
                case "--write-through":
                    writeThrough = true;
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
        if (host is null || share is null || mappings.Count == 0)
            throw new ArgumentException("--agent, --share and at least one --map are required.");
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
            WriteThrough = writeThrough,
            ConflictPolicy = conflictPolicy,
            CommandLine = string.Join(' ', args.Skip(index + 1).Select(Quote)),
        };
    }

    /// <summary>Quotes one argument for the usual (CommandLineToArgvW) parsing rules.</summary>
    private static string Quote(string argument)
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
