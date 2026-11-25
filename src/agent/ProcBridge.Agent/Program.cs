using System.Net;
using ProcBridge.Agent.Models;

namespace ProcBridge.Agent;

/// <summary>Console host: <c>ProcBridge.Agent --share name=D:\folder [--share ...] [--port 47800] [--bind 0.0.0.0]</c>.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var shares = new List<ReadOnlyShare>();
        var options = new AgentOptions();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing value for {args[i]}.");
                switch (args[i])
                {
                    case "--share":
                        int separator = value.IndexOf('=');
                        if (separator <= 0)
                            throw new ArgumentException("--share expects name=path.");
                        shares.Add(new ReadOnlyShare(value[..separator], value[(separator + 1)..]));
                        break;
                    case "--port":
                        options = options with { Port = int.Parse(value) };
                        break;
                    case "--bind":
                        options = options with { BindAddress = IPAddress.Parse(value) };
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument {args[i]}.");
                }
                i++;
            }
            if (shares.Count == 0)
                throw new ArgumentException("At least one --share name=path is required.");
        }
        catch (Exception e) when (e is ArgumentException or FormatException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(@"Usage: ProcBridge.Agent --share name=D:\folder [--share ...] [--port 47800] [--bind 0.0.0.0]");
            return 2;
        }

        await using var server = new AgentServer(shares, options);
        server.Start();
        Console.WriteLine($"ProcBridge.Agent listening on {options.BindAddress}:{server.Port} (read only, NOT encrypted: trusted LAN only).");
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
}
