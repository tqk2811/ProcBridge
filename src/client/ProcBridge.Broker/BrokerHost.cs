using System.Diagnostics;
using System.Runtime.Versioning;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Broker;

/// <summary>
/// One Broker session: client to the Agent, cache, change watcher and the named pipe for Hook.dll. Owns all of them.
/// The Launcher (or a test) creates it before starting the target process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping;
    private int _disposed;

    private BrokerHost(AgentClient client, FileCache cache, ChangeWatcher? watcher, HookIpcServer pipe, Task preload, CancellationTokenSource stopping)
    {
        _stopping = stopping;
        Client = client;
        Cache = cache;
        Watcher = watcher;
        Pipe = pipe;
        PreloadTask = preload;
    }

    public AgentClient Client { get; }

    public FileCache Cache { get; }

    /// <summary>Change watcher, or null when <see cref="BrokerOptions.WatchChanges"/> is off.</summary>
    public ChangeWatcher? Watcher { get; }

    public HookIpcServer Pipe { get; }

    /// <summary>Start-up tree walk (<see cref="BrokerOptions.PreloadTree"/>); never faults, a failure only means on-demand listing.</summary>
    public Task PreloadTask { get; }

    public static BrokerHost Start(BrokerOptions options)
    {
        var client = new AgentClient(new TcpConnectionFactory(options.AgentHost, options.AgentPort, options.Transport), options.ShareName, options.Transport);
        var cache = new FileCache(client, options.GetCacheRoot(), new FileCacheOptions
        {
            MetadataTtl = options.MetadataTtl,
            WatchedMetadataTtl = options.WatchedMetadataTtl,
        });

        ChangeWatcher? watcher = null;
        if (options.WatchChanges)
        {
            watcher = client.CreateWatcher(options.WatchReconnectDelay);
            cache.AttachWatcher(watcher);
            watcher.Start();
        }

        var pipe = new HookIpcServer(HookIpcProtocol.GetPipeName(options.SessionId), new HookRequestHandler(cache, options.Mappings));
        pipe.Start();

        var stopping = new CancellationTokenSource();
        Task preload = options.PreloadTree ? PreloadAsync(cache, watcher, options, stopping.Token) : Task.CompletedTask;
        return new BrokerHost(client, cache, watcher, pipe, preload, stopping);
    }

    private static async Task PreloadAsync(FileCache cache, ChangeWatcher? watcher, BrokerOptions options, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (watcher is not null)
        {
            // The watcher clears the cache when it subscribes; loading before that would be thrown away.
            try
            {
                await watcher.WaitActiveAsync(cancellationToken).WaitAsync(options.Transport.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No watch for now: preload anyway, the data lives for the normal TTL.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        // Nested mappings would walk the same folders twice; walking the outer one is enough.
        string[] roots = options.Mappings.Select(x => x.RemotePath.Trim('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        roots = roots.Where(x => !roots.Any(y => y != x && (y.Length == 0 || x.StartsWith(y + "/", StringComparison.OrdinalIgnoreCase)))).ToArray();
        foreach (string root in roots)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            try
            {
                await cache.PreloadTreeAsync(root, options.PreloadTreeDepth, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Trace.TraceWarning($"ProcBridge: tree preload of '{root}' failed: {e.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        await Pipe.DisposeAsync().ConfigureAwait(false);
        if (Watcher is not null)
            await Watcher.DisposeAsync().ConfigureAwait(false);
        try
        {
            await PreloadTask.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Reported by PreloadAsync already.
        }
        await Client.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
