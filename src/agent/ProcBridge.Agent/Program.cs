using System.Net;
using ProcBridge.Agent.Models;

namespace ProcBridge.Agent;

/// <summary>Console host: <c>ProcBridge.Agent --share name=D:\folder [--share ...] [--write-share name=D:\folder] [--port 47800] [--bind 0.0.0.0]</c>.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var shares = new List<AgentShare>();
        var options = new AgentOptions();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing value for {args[i]}.");
                switch (args[i])
                {
                    case "--share":
                    case "--write-share":
                        int separator = value.IndexOf('=');
                        if (separator <= 0)
                            throw new ArgumentException($"{args[i]} expects name=path.");
                        shares.Add(new AgentShare(value[..separator], value[(separator + 1)..], writable: args[i] == "--write-share"));
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
            Console.Error.WriteLine(@"Usage: ProcBridge.Agent --share name=D:\folder [--share ...] [--write-share name=D:\folder] [--port 47800] [--bind 0.0.0.0]");
            return 2;
        }

        await using var server = new AgentServer(shares, options);
        server.Start();
        Console.WriteLine($"ProcBridge.Agent listening on {options.BindAddress}:{server.Port} (NOT encrypted: trusted LAN only).");
        foreach (AgentShare share in shares)
            Console.WriteLine($"  share '{share.Name}' -> {share.RootPath} ({(share.IsWritable ? "read/write" : "read only")})");

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
