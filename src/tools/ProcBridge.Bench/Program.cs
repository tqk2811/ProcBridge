using System.Diagnostics;
using System.Globalization;
using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Bench;

/// <summary>
/// Loopback micro benchmark for M7: first stat of a whole tree (per-directory FsList vs one FsTree walk) and opening
/// many small files with and without read-ahead. Usage:
/// <c>ProcBridge.Bench [--dirs 50] [--files 40] [--size 2048] [--runs 3]</c>.
/// </summary>
internal static class Program
{
    private static readonly TransportOptions Transport = new();

    private static async Task<int> Main(string[] args)
    {
        int dirs = 50, files = 40, size = 2048, runs = 3;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            int value = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "--dirs": dirs = value; break;
                case "--files": files = value; break;
                case "--size": size = value; break;
                case "--runs": runs = value; break;
                default:
                    Console.Error.WriteLine($"Unknown argument {args[i]}.");
                    return 2;
            }
        }

        string root = Path.Combine(Path.GetTempPath(), "pb-bench-" + Guid.NewGuid().ToString("N"));
        string share = Path.Combine(root, "share");
        try
        {
            string[] paths = CreateTree(share, dirs, files, size);
            Console.WriteLine($"Tree: {dirs} directories x {files} files of {size} bytes = {paths.Length} files, loopback TCP.");

            await using var agent = new AgentServer([new ReadOnlyShare("src", share)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
            agent.Start();

            for (int run = 1; run <= runs; run++)
            {
                Console.WriteLine($"Run {run}:");
                await MeasureAsync(agent.Port, root, "  stat tree, FsList per directory", cache => ListRecursiveAsync(cache, string.Empty));
                await MeasureAsync(agent.Port, root, "  stat tree, one FsTree walk     ", cache => cache.PreloadTreeAsync(string.Empty, 0, CancellationToken.None));
                await MeasureAsync(agent.Port, root, "  open all files, no read-ahead  ", cache => OpenAllAsync(cache, paths), readAhead: 0);
                await MeasureAsync(agent.Port, root, "  open all files, read-ahead     ", cache => OpenAllAsync(cache, paths), readAhead: Math.Max(size, 1));
            }
            return 0;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static string[] CreateTree(string share, int dirs, int files, int size)
    {
        var paths = new List<string>();
        byte[] content = new byte[size];
        new Random(1).NextBytes(content);
        for (int d = 0; d < dirs; d++)
        {
            // Two levels so the walk has some depth: group/dir/file.
            string relativeDir = $"g{d / 10}/d{d}";
            Directory.CreateDirectory(Path.Combine(share, relativeDir));
            for (int f = 0; f < files; f++)
            {
                string relative = $"{relativeDir}/file{f}.cs";
                File.WriteAllBytes(Path.Combine(share, relative), content);
                paths.Add(relative);
            }
        }
        return paths.ToArray();
    }

    /// <summary>Runs <paramref name="action"/> against a cold cache (new client, new cache folder) and prints the time.</summary>
    private static async Task MeasureAsync(int port, string root, string label, Func<FileCache, Task> action, long readAhead = 0)
    {
        string cacheRoot = Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N"));
        await using var client = new AgentClient(new TcpConnectionFactory("127.0.0.1", port, Transport), "src", Transport);
        var cache = new FileCache(client, cacheRoot, new FileCacheOptions { MetadataTtl = TimeSpan.FromMinutes(5), ReadAheadMaxFileSize = readAhead });
        // Open the pool before timing so every case pays the same connect cost.
        await cache.StatAsync(string.Empty, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        await action(cache);
        watch.Stop();
        long requests = client.Pool.GetRequestsPerSlot().Sum() - 1;
        Console.WriteLine($"{label}: {watch.Elapsed.TotalMilliseconds,8:0.0} ms, {requests,6} pool requests, {cache.ReadAheadDownloads,5} read ahead");
        await cache.WaitForReadAheadAsync();
        DeleteQuietly(cacheRoot);
    }

    private static async Task ListRecursiveAsync(FileCache cache, string path)
    {
        IReadOnlyList<FsEntry> entries = await cache.ListAsync(path, CancellationToken.None);
        foreach (FsEntry entry in entries.Where(x => x.IsDirectory))
            await ListRecursiveAsync(cache, path.Length == 0 ? entry.Name : path + "/" + entry.Name);
    }

    /// <summary>Opens the files one after the other, like a compiler reading its sources.</summary>
    private static async Task OpenAllAsync(FileCache cache, string[] paths)
    {
        foreach (string path in paths)
            await cache.EnsureAsync(path, CancellationToken.None);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
