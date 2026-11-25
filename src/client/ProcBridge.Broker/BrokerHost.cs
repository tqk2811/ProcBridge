using System.Diagnostics;
using System.Runtime.Versioning;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Transport;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Broker;

/// <summary>
/// One Broker session: client to the Agent, cache, change watcher and the named pipe for Hook.dll. Owns all of them.
/// The Launcher (or a test) creates it before starting the target process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping;
    private readonly IDisposable? _factory;
    private int _disposed;

    private BrokerHost(AgentClient client, FileCache cache, ChangeWatcher? watcher, WriteBackQueue writeBack, HookIpcServer pipe, Task preload, CancellationTokenSource stopping, IDisposable? factory)
    {
        _factory = factory;
        _stopping = stopping;
        Client = client;
        Cache = cache;
        Watcher = watcher;
        WriteBack = writeBack;
        Pipe = pipe;
        PreloadTask = preload;
    }

    public AgentClient Client { get; }

    public FileCache Cache { get; }

    /// <summary>Change watcher, or null when <see cref="BrokerOptions.WatchChanges"/> is off.</summary>
    public ChangeWatcher? Watcher { get; }

    /// <summary>Write-back of changed cache copies and its persistent queue (M3).</summary>
    public WriteBackQueue WriteBack { get; }

    public HookIpcServer Pipe { get; }

    /// <summary>Start-up tree walk (<see cref="BrokerOptions.PreloadTree"/>); never faults, a failure only means on-demand listing.</summary>
    public Task PreloadTask { get; }

    public static BrokerHost Start(BrokerOptions options)
    {
        IConnectionFactory factory = CreateConnectionFactory(options);
        var client = new AgentClient(factory, options.ShareName, options.Transport);
        var cache = new FileCache(client, options.GetCacheRoot(), new FileCacheOptions
        {
            MetadataTtl = options.MetadataTtl,
            WatchedMetadataTtl = options.WatchedMetadataTtl,
            ReadAheadMaxFileSize = options.ReadAheadMaxFileSize,
            ReadAheadMaxFiles = options.ReadAheadMaxFiles,
            ReadAheadParallelism = options.ReadAheadParallelism,
        });

        ChangeWatcher? watcher = null;
        if (options.WatchChanges)
        {
            watcher = client.CreateWatcher(options.WatchReconnectDelay);
            cache.AttachWatcher(watcher);
            watcher.Start();
        }

        var writeBack = new WriteBackQueue(client, cache, options.GetWriteQueueRoot(), options.GetWriteTarget(), options.ConflictPolicy, options.WriteRetryDelay);
        var handler = new HookRequestHandler(cache, options.Mappings, writeBack, options.WriteThrough);
        var pipe = new HookIpcServer(HookIpcProtocol.GetPipeName(options.SessionId), handler);
        pipe.Start();
        writeBack.StartRecovery();

        var stopping = new CancellationTokenSource();
        Task preload = options.PreloadTree ? PreloadAsync(cache, watcher, options, stopping.Token) : Task.CompletedTask;
        return new BrokerHost(client, cache, watcher, writeBack, pipe, preload, stopping, factory as IDisposable);
    }

    /// <summary>Encrypted factory when <see cref="BrokerOptions.Security"/> is set; plain only when explicitly allowed.</summary>
    /// <exception cref="ArgumentException">No security and plain transport not allowed.</exception>
    public static IConnectionFactory CreateConnectionFactory(BrokerOptions options)
    {
        if (options.Security is { } security)
        {
            return new SecureConnectionFactory(new TcpStreamConnector(options.AgentHost, options.AgentPort), security.Identity,
                security.PairedMachines, security.AgentPublicKey, options.Transport);
        }
        if (!options.AllowPlaintextTransport)
            throw new ArgumentException("The Broker needs BrokerSecurity (paired Agent); plain transport is for development only.", nameof(options));
        return new TcpConnectionFactory(options.AgentHost, options.AgentPort, options.Transport);
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
        // Last write-backs first, while the connection is still up.
        try
        {
            await WriteBack.SyncOpenAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Trace.TraceError($"ProcBridge: final write-back failed: {e.Message}");
        }
        await WriteBack.DisposeAsync().ConfigureAwait(false);
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
        _factory?.Dispose();
        _stopping.Dispose();
    }
}
