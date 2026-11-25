using System.Net;
using ProcBridge.Agent.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Console host. Serve: <c>ProcBridge.Agent --share name=D:\folder [--share ...] [--port 47800] [--bind 0.0.0.0]
/// [--allow-pairing] [--data dir] [--plaintext]</c>. Manage paired machines: <c>--list-paired</c>, <c>--revoke &lt;fingerprint&gt;</c>.
/// The identity key and the paired machine list live in <c>%LOCALAPPDATA%\ProcBridge\agent</c> (DPAPI) unless --data says otherwise.
/// </summary>
internal static class Program
{
    private const string Usage =
        @"Usage: ProcBridge.Agent --share name=D:\folder [--share ...] [--port 47800] [--bind 0.0.0.0] [--allow-pairing] [--data <dir>] [--plaintext]" + "\n" +
        @"       ProcBridge.Agent --list-paired [--data <dir>]" + "\n" +
        @"       ProcBridge.Agent --revoke <fingerprint> [--data <dir>]" + "\n" +
        @"  --plaintext serves unencrypted frames to anyone on the network (development only).";

    private static async Task<int> Main(string[] args)
    {
        var shares = new List<ReadOnlyShare>();
        var options = new AgentOptions();
        bool allowPairing = false;
        bool listPaired = false;
        string? revoke = null;
        string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "agent");
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}.");
                switch (args[i])
                {
                    case "--share":
                        string value = Next();
                        int separator = value.IndexOf('=');
                        if (separator <= 0)
                            throw new ArgumentException("--share expects name=path.");
                        shares.Add(new ReadOnlyShare(value[..separator], value[(separator + 1)..]));
                        break;
                    case "--port":
                        options = options with { Port = int.Parse(Next()) };
                        break;
                    case "--bind":
                        options = options with { BindAddress = IPAddress.Parse(Next()) };
                        break;
                    case "--data":
                        dataDirectory = Next();
                        break;
                    case "--allow-pairing":
                        allowPairing = true;
                        break;
                    case "--plaintext":
                        options = options with { AllowPlaintextTransport = true };
                        break;
                    case "--list-paired":
                        listPaired = true;
                        break;
                    case "--revoke":
                        revoke = Next();
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument {args[i]}.");
                }
            }
            if (shares.Count == 0 && !listPaired && revoke is null)
                throw new ArgumentException("At least one --share name=path is required.");
            if (allowPairing && options.AllowPlaintextTransport)
                throw new ArgumentException("--allow-pairing needs the encrypted transport (drop --plaintext).");
        }
        catch (Exception e) when (e is ArgumentException or FormatException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var protector = new DpapiKeyProtector();
        var pairedMachines = new PairedMachineStore(Path.Combine(dataDirectory, "paired-machines.bin"), protector);
        if (listPaired)
        {
            foreach (PairedMachine machine in pairedMachines.List())
                Console.WriteLine($"{machine.Fingerprint}  {machine.Name}  paired {machine.PairedAtUtc:u}");
            return 0;
        }
        if (revoke is not null)
        {
            bool removed = pairedMachines.Revoke(revoke);
            Console.WriteLine(removed ? $"Revoked {revoke}." : $"{revoke} is not paired.");
            return removed ? 0 : 1;
        }

        using IdentityKey? identity = options.AllowPlaintextTransport ? null : new IdentityKeyStore(Path.Combine(dataDirectory, "identity.key"), protector).LoadOrCreate();
        AgentSecurity? security = identity is null ? null : new AgentSecurity { Identity = identity, PairedMachines = pairedMachines, AllowPairing = allowPairing };
        await using var server = new AgentServer(shares, options, security);
        server.PairingRequested += AskUserToPair;
        server.Start();
        Console.WriteLine(security is null
            ? $"ProcBridge.Agent listening on {options.BindAddress}:{server.Port} (read only, NOT encrypted: development only)."
            : $"ProcBridge.Agent listening on {options.BindAddress}:{server.Port} (read only, encrypted). Fingerprint {server.Fingerprint}.");
        foreach (ReadOnlyShare share in shares)
            Console.WriteLine($"  share '{share.Name}' -> {share.RootPath}");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        await stop.Task;
        return 0;
    }

    /// <summary>Console stand-in for the WPF pairing dialog (plan 15, question 2).</summary>
    private static void AskUserToPair(PairingRequest request)
    {
        _ = Task.Run(() =>
        {
            Console.WriteLine($"Pairing request from {request.PeerName} ({request.RemoteAddress}).");
            Console.WriteLine($"  Code to type on that machine: {request.Code}");
            Console.WriteLine($"  Its fingerprint (compare with what it shows): {request.PeerFingerprint}");
            Console.Write("Accept? [y/N] ");
            bool accept = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
            if (accept)
                request.Accept();
            else
                request.Reject();
        });
    }
}
