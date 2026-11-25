using System.Runtime.Versioning;
using ProcBridge.Broker.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Launcher.Models;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher;

/// <summary>Console entry point, see <see cref="LauncherCommandLine.Usage"/>.</summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--pair" or "--list-paired" or "--revoke")
            return await RunTrustCommandAsync(args);

        LauncherOptions options;
        try
        {
            options = LauncherCommandLine.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(LauncherCommandLine.Usage);
            return 2;
        }

        using IdentityKey? identity = options.AllowPlaintextTransport ? null : OpenIdentity(options.DataDirectory);
        if (identity is not null)
        {
            PairedMachineStore store = OpenPairedMachines(options.DataDirectory);
            PairedMachine? agent = options.AgentFingerprint is not null
                ? store.Find(options.AgentFingerprint)
                : store.FindByAddress($"{options.AgentHost}:{options.AgentPort}");
            if (agent is null)
            {
                Console.Error.WriteLine($"ProcBridge.Launcher: no paired Agent for {options.AgentHost}:{options.AgentPort}; run --pair first.");
                return 2;
            }
            options = options with { Security = new BrokerSecurity { Identity = identity, PairedMachines = store, AgentPublicKey = agent.PublicKey } };
        }

        // Ctrl+C reaches the target (same console); the Launcher keeps the Broker up until the target exits.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        try
        {
            return await new LaunchSession(options).RunAsync();
        }
        catch (FileNotFoundException e)
        {
            Console.Error.WriteLine($"ProcBridge.Launcher: {e.Message} {e.FileName}");
            return 2;
        }
    }

    /// <summary><c>--pair --agent host[:port]</c>, <c>--list-paired</c>, <c>--revoke &lt;fingerprint&gt;</c>, each with optional <c>--data</c>.</summary>
    private static async Task<int> RunTrustCommandAsync(string[] args)
    {
        string? agent = null;
        string? revoke = null;
        string? dataDirectory = null;
        for (int i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}.");
            switch (args[i])
            {
                case "--agent":
                    agent = Next();
                    break;
                case "--data":
                    dataDirectory = Next();
                    break;
                default:
                    if (args[0] == "--revoke" && revoke is null)
                    {
                        revoke = args[i];
                        break;
                    }
                    Console.Error.WriteLine($"Unknown argument {args[i]}.");
                    Console.Error.WriteLine(LauncherCommandLine.Usage);
                    return 2;
            }
        }

        PairedMachineStore store = OpenPairedMachines(dataDirectory);
        switch (args[0])
        {
            case "--list-paired":
                foreach (PairedMachine machine in store.List())
                    Console.WriteLine($"{machine.Fingerprint}  {machine.Name}  {machine.Address}  paired {machine.PairedAtUtc:u}");
                return 0;
            case "--revoke":
                if (revoke is null)
                {
                    Console.Error.WriteLine("--revoke expects a fingerprint.");
                    return 2;
                }
                bool removed = store.Revoke(revoke);
                Console.WriteLine(removed ? $"Revoked {revoke}." : $"{revoke} is not paired.");
                return removed ? 0 : 1;
        }

        if (agent is null)
        {
            Console.Error.WriteLine("--pair needs --agent <host>[:port].");
            return 2;
        }
        int colon = agent.LastIndexOf(':');
        (string host, int port) = colon > 0 && int.TryParse(agent[(colon + 1)..], out int parsed)
            ? (agent[..colon], parsed)
            : (agent, Protocol.ProtocolInfo.DefaultPort);
        using IdentityKey identity = OpenIdentity(dataDirectory);
        var client = new PairingClient(identity, store);
        Console.WriteLine($"This machine's fingerprint: {client.Fingerprint}");
        try
        {
            PairedMachine paired = await client.PairAsync(new TcpStreamConnector(host, port), (fingerprint, _) =>
            {
                Console.WriteLine($"Agent fingerprint (compare with what the Agent shows): {fingerprint}");
                Console.Write("Code shown on the Agent: ");
                return Task.FromResult(Console.ReadLine() ?? string.Empty);
            }, CancellationToken.None);
            Console.WriteLine($"Paired with {paired.Name} ({paired.Fingerprint}).");
            return 0;
        }
        catch (Exception e) when (e is PairingFailedException or IOException or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Pairing failed: {e.Message}");
            return 1;
        }
    }

    private static string DataDirectory(string? dataDirectory)
        => dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "launcher");

    private static IdentityKey OpenIdentity(string? dataDirectory)
        => new IdentityKeyStore(Path.Combine(DataDirectory(dataDirectory), "identity.key"), new DpapiKeyProtector()).LoadOrCreate();

    private static PairedMachineStore OpenPairedMachines(string? dataDirectory)
        => new(Path.Combine(DataDirectory(dataDirectory), "paired-machines.bin"), new DpapiKeyProtector());
}
