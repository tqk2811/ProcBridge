using System.Diagnostics;
using System.Globalization;
using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;
using ProcBridge.HookIpc.Models;

namespace ProcBridge.Bench;

/// <summary>
/// Benchmark of the Broker as the hook sees it (Hook IPC requests straight to the request handler), over a simulated
/// network link between Broker and Agent (<see cref="LatencyProxy"/>). Each case starts a new Broker on an empty cache.
/// Usage: <c>ProcBridge.Bench [--profile loopback|lan|wan|far|all] [--rtt ms --mbps n] [--dirs 20] [--files 25]
/// [--size 4096] [--big-mb 32] [--runs 2]</c>.
/// </summary>
internal static class Program
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileOpen = 1;
    private const long ClientId = 1;

    private static readonly (string Name, double RttMs, double Mbps)[] Profiles =
    [
        ("loopback", 0, 0),
        ("lan", 1, 1000),
        ("wan", 40, 50),
        ("far", 100, 10),
    ];

    private static async Task<int> Main(string[] args)
    {
        var options = new BenchOptions();
        string profile = "all";
        double? rtt = null, mbps = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--profile": profile = value; break;
                case "--rtt": rtt = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--mbps": mbps = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--dirs": options.Dirs = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--files": options.Files = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--size": options.Size = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--big-mb": options.BigMegabytes = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--runs": options.Runs = int.Parse(value, CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine($"Unknown argument {args[i]}.");
                    return 2;
            }
        }
        (string Name, double RttMs, double Mbps)[] links = rtt is not null || mbps is not null
            ? [("custom", rtt ?? 0, mbps ?? 0)]
            : profile == "all" ? Profiles : Profiles.Where(x => x.Name == profile).ToArray();
        if (links.Length == 0)
        {
            Console.Error.WriteLine($"Unknown profile {profile}.");
            return 2;
        }

        string root = Path.Combine(Path.GetTempPath(), "pb-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bench = new Bench(root, options);
            Console.WriteLine($"Tree: {options.Dirs} directories x {options.Files} files of {options.Size} bytes, big file {options.BigMegabytes} MB.");
            await using var agent = new AgentServer([new AgentShare("src", bench.Share, writable: true)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
            agent.Start();
            foreach ((string name, double rttMs, double linkMbps) in links)
            {
                await using var proxy = new LatencyProxy(new IPEndPoint(IPAddress.Loopback, agent.Port), TimeSpan.FromMilliseconds(rttMs), linkMbps * 1_000_000 / 8);
                Console.WriteLine();
                Console.WriteLine($"Link {name}: RTT {rttMs} ms, {(linkMbps > 0 ? linkMbps + " Mbit/s" : "unlimited")} each way.");
                for (int run = 1; run <= options.Runs; run++)
                {
                    Console.WriteLine($" Run {run}:");
                    await bench.RunAllAsync(proxy);
                }
            }
            return 0;
        }
        finally
        {
            Bench.DeleteQuietly(root);
        }
    }

    private sealed class BenchOptions
    {
        public int Dirs { get; set; } = 20;
        public int Files { get; set; } = 25;
        public int Size { get; set; } = 4096;
        public int BigMegabytes { get; set; } = 32;
        public int Runs { get; set; } = 2;
    }

    /// <summary>The share on machine B and the cases measured against it.</summary>
    private sealed class Bench
    {
        private const int ScatteredReads = 16;
        private const int ScatteredReadSize = 64 * 1024;
        private const int WrittenFiles = 20;
        private const int WriteBigMegabytes = 8;

        private readonly string _root;
        private readonly BenchOptions _options;
        private readonly string[] _directories;
        private readonly string[] _files;
        private readonly byte[] _buffer = new byte[HookIpcProtocol.MaxMessageSize];
        private readonly byte[] _reply = new byte[HookIpcProtocol.MaxMessageSize];

        public Bench(string root, BenchOptions options)
        {
            _root = root;
            _options = options;
            Share = Path.Combine(root, "share");
            var directories = new List<string> { string.Empty };
            var files = new List<string>();
            byte[] content = new byte[options.Size];
            new Random(1).NextBytes(content);
            for (int d = 0; d < options.Dirs; d++)
            {
                // Two levels so a walk has some depth: group\dir\file.
                string group = $"g{d / 10}";
                if (!directories.Contains(group))
                    directories.Add(group);
                string directory = $@"{group}\d{d}";
                directories.Add(directory);
                Directory.CreateDirectory(Path.Combine(Share, "proj", directory));
                for (int f = 0; f < options.Files; f++)
                {
                    string file = $@"{directory}\file{f}.cs";
                    File.WriteAllBytes(Path.Combine(Share, "proj", file), content);
                    files.Add(file);
                }
            }
            byte[] big = new byte[1024 * 1024];
            using (FileStream stream = File.Create(Path.Combine(Share, "proj", "big.bin")))
            {
                for (int i = 0; i < options.BigMegabytes; i++)
                {
                    new Random(i).NextBytes(big);
                    stream.Write(big);
                }
            }
            _directories = directories.ToArray();
            _files = files.ToArray();
        }

        public string Share { get; }

        public async Task RunAllAsync(LatencyProxy proxy)
        {
            await MeasureAsync(proxy, "list every directory, no preload       ", preload: false, ListAllAsync);
            await MeasureAsync(proxy, "tree preload + list every directory    ", preload: true, ListAllAsync, timePreload: true);
            await MeasureAsync(proxy, "open small files, no read-ahead        ", preload: true, OpenSmallAsync);
            await MeasureAsync(proxy, "open small files, read-ahead x2        ", preload: true, OpenSmallAsync, readAhead: 64 * 1024, readAheadParallelism: 2);
            await MeasureAsync(proxy, "open small files, read-ahead x8        ", preload: true, OpenSmallAsync, readAhead: 64 * 1024, readAheadParallelism: 8);
            await MeasureAsync(proxy, $"read big file whole ({_options.BigMegabytes} MB)           ", preload: true, ReadBigWholeAsync);
            await MeasureAsync(proxy, $"read big file, {ScatteredReads} x 64 KB scattered   ", preload: true, ReadBigScatteredAsync);
            await MeasureAsync(proxy, $"write back {WrittenFiles} small files           ", preload: true, WriteSmallAsync);
            await MeasureAsync(proxy, $"write back one new {WriteBigMegabytes} MB file          ", preload: true, WriteBigAsync);
        }

        /// <summary>
        /// Starts a Broker on an empty cache through the proxy, waits for its start-up work (timed too when
        /// <paramref name="timePreload"/>), then times <paramref name="action"/>.
        /// </summary>
        private async Task MeasureAsync(LatencyProxy proxy, string label, bool preload, Func<BrokerHost, Task> action, bool timePreload = false,
            long readAhead = 0, int readAheadParallelism = 2)
        {
            string cacheRoot = Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N"));
            BrokerHost broker = BrokerHost.Start(new BrokerOptions
            {
                AllowPlaintextTransport = true,
                SessionId = HookIpcProtocol.NewSessionId(),
                AgentHost = "127.0.0.1",
                AgentPort = proxy.Port,
                ShareName = "src",
                Mappings = [new VirtualMapping(@"D:\virtual", "proj")],
                CacheRoot = cacheRoot,
                PreloadTree = preload,
                ReadAheadMaxFileSize = readAhead,
                ReadAheadParallelism = readAheadParallelism,
                // Small enough that the big file is cached by ranges (M8).
                RangeThreshold = 16L * 1024 * 1024,
                HoldShareMode = false,
            });
            try
            {
                long up = proxy.BytesUp, down = proxy.BytesDown, requests = Requests(broker);
                var watch = Stopwatch.StartNew();
                await broker.PreloadTask;
                if (broker.Watcher is not null)
                    await broker.Watcher.WaitActiveAsync(CancellationToken.None);
                if (!timePreload)
                {
                    up = proxy.BytesUp;
                    down = proxy.BytesDown;
                    requests = Requests(broker);
                    watch.Restart();
                }
                broker.Pipe.Handler.ClientConnected(ClientId);
                await action(broker);
                watch.Stop();
                Console.WriteLine($"  {label}: {watch.Elapsed.TotalMilliseconds,9:0.0} ms, {(proxy.BytesUp - up) / 1024.0,8:0} KB up, {(proxy.BytesDown - down) / 1024.0,8:0} KB down, {Requests(broker) - requests,5} requests");
                await broker.Cache.WaitForReadAheadAsync();
                broker.Pipe.Handler.ClientDisconnected(ClientId);
            }
            finally
            {
                await broker.DisposeAsync();
                DeleteQuietly(cacheRoot);
            }
        }

        /// <summary>Requests the Broker sent on its pool so far (the change watcher has a connection of its own).</summary>
        private static long Requests(BrokerHost broker) => broker.Client.Pool.GetRequestsPerSlot().Sum();

        private async Task ListAllAsync(BrokerHost broker)
        {
            foreach (string directory in _directories)
            {
                uint start = 0;
                while (true)
                {
                    HookListReply page = await CallAsync(broker, new HookListRequest { MappingIndex = 0, StartIndex = start, RelativePath = directory }, HookListReply.Read);
                    start += (uint)page.Entries.Count;
                    if (!page.HasMore)
                        break;
                }
            }
        }

        /// <summary>Opens the files one after the other, like a compiler reading its sources.</summary>
        private async Task OpenSmallAsync(BrokerHost broker)
        {
            foreach (string file in _files)
                await ResolveAsync(broker, file, GenericRead);
        }

        private async Task ReadBigWholeAsync(BrokerHost broker)
        {
            HookResolveReply open = await ResolveAsync(broker, "big.bin", GenericRead);
            if (open.Info.RangeId != 0)
                await CallAsync(broker, new HookFetchRangeRequest { RangeId = open.Info.RangeId, Flags = HookIpcProtocol.FetchFlagWhole, Offset = 0, Length = open.Info.Size }, HookFetchRangeReply.Read);
        }

        private async Task ReadBigScatteredAsync(BrokerHost broker)
        {
            HookResolveReply open = await ResolveAsync(broker, "big.bin", GenericRead);
            if (open.Info.RangeId == 0)
                throw new InvalidOperationException("The big file is not cached by ranges.");
            long step = open.Info.Size / ScatteredReads;
            for (int i = 0; i < ScatteredReads; i++)
                await CallAsync(broker, new HookFetchRangeRequest { RangeId = open.Info.RangeId, Offset = i * step + 4096, Length = ScatteredReadSize }, HookFetchRangeReply.Read);
        }

        private async Task WriteSmallAsync(BrokerHost broker)
        {
            var opening = new Stopwatch();
            var closing = new Stopwatch();
            for (int i = 0; i < WrittenFiles; i++)
            {
                opening.Start();
                HookResolveReply open = await ResolveAsync(broker, _files[i], GenericRead | GenericWrite);
                opening.Stop();
                await File.AppendAllTextAsync(open.CachePath, "// changed on A\r\n");
                closing.Start();
                await CallAsync(broker, new HookTokenRequest { Type = HookIpcMessageType.Close, WriteToken = open.WriteToken }, (ref HookIpcReader _) => 0);
                closing.Stop();
            }
            Console.Write($"  (per file: open {opening.Elapsed.TotalMilliseconds / WrittenFiles:0.0} ms, close {closing.Elapsed.TotalMilliseconds / WrittenFiles:0.0} ms)");
        }

        private async Task WriteBigAsync(BrokerHost broker)
        {
            string name = $"written-{Guid.NewGuid():N}.bin";
            HookResolveReply open = await CallAsync(broker, new HookResolveRequest
            {
                MappingIndex = 0,
                RelativePath = name,
                DesiredAccess = GenericRead | GenericWrite,
                CreateDisposition = 2, // FILE_CREATE
            }, HookResolveReply.Read);
            byte[] data = new byte[WriteBigMegabytes * 1024 * 1024];
            new Random(7).NextBytes(data);
            await File.WriteAllBytesAsync(open.CachePath, data);
            await CallAsync(broker, new HookTokenRequest { Type = HookIpcMessageType.Close, WriteToken = open.WriteToken }, (ref HookIpcReader _) => 0);
        }

        private Task<HookResolveReply> ResolveAsync(BrokerHost broker, string path, uint access)
            => CallAsync(broker, new HookResolveRequest { MappingIndex = 0, RelativePath = path, DesiredAccess = access, CreateDisposition = FileOpen }, HookResolveReply.Read);

        private delegate T ReplyReader<out T>(ref HookIpcReader reader);

        private async Task<T> CallAsync<T>(BrokerHost broker, IHookIpcMessage request, ReplyReader<T> read)
        {
            int size = HookIpcCodec.Encode(_buffer, 1, request);
            int length = await broker.Pipe.Handler.HandleAsync(ClientId, _buffer.AsMemory(0, size), _reply, CancellationToken.None);
            HookIpcHeader header = HookIpcCodec.ReadHeader(_reply.AsSpan(0, length));
            if (header.Status != 0)
                throw new InvalidOperationException($"{request.MessageType} failed with 0x{header.Status:X8}.");
            var reader = new HookIpcReader(_reply.AsSpan(0, length));
            return read(ref reader);
        }

        public static void DeleteQuietly(string path)
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
}
