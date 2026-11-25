using System.Net;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Console host. Serve: <c>ProcBridge.Agent --share name=D:\folder [--share ...] [--port 47800] [--bind 0.0.0.0]
/// [--allow-pairing] [--data dir] [--plaintext]</c>. Manage paired machines: <c>--list-paired</c>, <c>--revoke &lt;fingerprint&gt;</c>.
/// Remote Explorer profile of a paired machine: <c>--explorer &lt;fingerprint&gt; [--file-root dir]... [--registry-root key]...
/// [--permissions FileRead,ProcessRead|None]</c> replaces it, <c>--show-explorer</c> prints it.
/// The identity key, the paired machine list and the Explorer profiles live in <c>%LOCALAPPDATA%\ProcBridge\agent</c> (DPAPI) unless --data says otherwise.
/// </summary>
internal static class Program
{
    private const string Usage =
        @"Usage: ProcBridge.Agent --share name=D:\folder [--share ...] [--port 47800] [--bind 0.0.0.0] [--allow-pairing] [--data <dir>] [--plaintext]" + "\n" +
        @"       ProcBridge.Agent --list-paired [--data <dir>]" + "\n" +
        @"       ProcBridge.Agent --revoke <fingerprint> [--data <dir>]" + "\n" +
        @"       ProcBridge.Agent --explorer <fingerprint> [--file-root <dir>]... [--registry-root <key>]... [--permissions <flag,flag|None>] [--data <dir>]" + "\n" +
        @"       ProcBridge.Agent --show-explorer <fingerprint> [--data <dir>]" + "\n" +
        @"  --explorer replaces the whole profile; flags: " + "FileRead, FileWrite, ProcessRead, ProcessKill, RegistryRead, RegistryWrite, ServiceRead, ServiceControl, SystemInfo." + "\n" +
        @"  --plaintext serves unencrypted frames to anyone on the network (development only).";

    private static async Task<int> Main(string[] args)
    {
        var shares = new List<ReadOnlyShare>();
        var options = new AgentOptions();
        bool allowPairing = false;
        bool listPaired = false;
        string? revoke = null;
        string? explorerTarget = null;
        string? showExplorer = null;
        var fileRoots = new List<string>();
        var registryRoots = new List<string>();
        ExplorerPermissions permissions = ExplorerPermissions.None;
        string dataDirectory = AgentDataFiles.DefaultDirectory;
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
                    case "--explorer":
                        explorerTarget = Next();
                        break;
                    case "--show-explorer":
                        showExplorer = Next();
                        break;
                    case "--file-root":
                        fileRoots.Add(Path.GetFullPath(Next()));
                        break;
                    case "--registry-root":
                        registryRoots.Add(Next());
                        break;
                    case "--permissions":
                        permissions = Enum.Parse<ExplorerPermissions>(Next(), ignoreCase: true);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument {args[i]}.");
                }
            }
            if ((fileRoots.Count > 0 || registryRoots.Count > 0 || permissions != ExplorerPermissions.None) && explorerTarget is null)
                throw new ArgumentException("--file-root, --registry-root and --permissions go with --explorer <fingerprint>.");
            if (shares.Count == 0 && !listPaired && revoke is null && explorerTarget is null && showExplorer is null)
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
        var pairedMachines = new PairedMachineStore(Path.Combine(dataDirectory, AgentDataFiles.PairedMachines), protector);
        var explorerPolicies = new ExplorerPolicyStore(Path.Combine(dataDirectory, AgentDataFiles.ExplorerPolicies), protector);
        if (explorerTarget is not null)
        {
            if (pairedMachines.Find(explorerTarget) is null)
            {
                Console.Error.WriteLine($"{explorerTarget} is not paired.");
                return 1;
            }
            explorerPolicies.Set(explorerTarget, new ExplorerPolicy { FileRoots = fileRoots, RegistryRoots = registryRoots, Permissions = permissions });
            Console.WriteLine($"Explorer profile of {explorerTarget} saved (applies to new connections).");
            PrintPolicy(explorerPolicies.Get(explorerTarget));
            return 0;
        }
        if (showExplorer is not null)
        {
            PrintPolicy(explorerPolicies.Get(showExplorer));
            return 0;
        }
        if (listPaired)
        {
            foreach (PairedMachine machine in pairedMachines.List())
                Console.WriteLine($"{machine.Fingerprint}  {machine.Name}  paired {machine.PairedAtUtc:u}");
            return 0;
        }
        if (revoke is not null)
        {
            bool removed = pairedMachines.Revoke(revoke);
            explorerPolicies.Remove(revoke);
            Console.WriteLine(removed ? $"Revoked {revoke}." : $"{revoke} is not paired.");
            return removed ? 0 : 1;
        }

        using IdentityKey? identity = options.AllowPlaintextTransport ? null : new IdentityKeyStore(Path.Combine(dataDirectory, AgentDataFiles.IdentityKey), protector).LoadOrCreate();
        AgentSecurity? security = identity is null ? null : new AgentSecurity { Identity = identity, PairedMachines = pairedMachines, AllowPairing = allowPairing, ExplorerPolicies = explorerPolicies };
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

    private static void PrintPolicy(ExplorerPolicy policy)
    {
        Console.WriteLine($"  permissions: {policy.Permissions}");
        Console.WriteLine($"  file roots: {(policy.FileRoots.Count == 0 ? "(none)" : string.Join("; ", policy.FileRoots))}");
        Console.WriteLine($"  registry roots: {(policy.RegistryRoots.Count == 0 ? "(none)" : string.Join("; ", policy.RegistryRoots))}");
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
