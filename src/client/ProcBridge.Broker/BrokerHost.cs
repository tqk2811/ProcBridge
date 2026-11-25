using System.Runtime.Versioning;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Broker;

/// <summary>
/// One Broker session: client to the Agent, cache, and the named pipe for Hook.dll. Owns all three.
/// The Launcher (or a test) creates it before starting the target process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerHost : IAsyncDisposable
{
    private BrokerHost(AgentClient client, FileCache cache, WriteBackQueue writeBack, HookIpcServer pipe)
    {
        Client = client;
        Cache = cache;
        WriteBack = writeBack;
        Pipe = pipe;
    }

    public AgentClient Client { get; }

    public FileCache Cache { get; }

    /// <summary>Write-back of changed cache copies and its persistent queue (M3).</summary>
    public WriteBackQueue WriteBack { get; }

    public HookIpcServer Pipe { get; }

    public static BrokerHost Start(BrokerOptions options)
    {
        var client = new AgentClient(new TcpConnectionFactory(options.AgentHost, options.AgentPort, options.Transport), options.ShareName, options.Transport);
        var cache = new FileCache(client, options.GetCacheRoot(), options.MetadataTtl);
        var writeBack = new WriteBackQueue(client, cache, options.GetWriteQueueRoot(), options.GetWriteTarget(), options.ConflictPolicy, options.WriteRetryDelay);
        var handler = new HookRequestHandler(cache, options.Mappings, writeBack, options.WriteThrough);
        var pipe = new HookIpcServer(HookIpcProtocol.GetPipeName(options.SessionId), handler);
        pipe.Start();
        writeBack.StartRecovery();
        return new BrokerHost(client, cache, writeBack, pipe);
    }

    public async ValueTask DisposeAsync()
    {
        await Pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await WriteBack.SyncOpenAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError($"ProcBridge: final write-back failed: {e.Message}");
        }
        await WriteBack.DisposeAsync().ConfigureAwait(false);
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
