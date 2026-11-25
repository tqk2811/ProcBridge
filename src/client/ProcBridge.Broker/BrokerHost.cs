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
    private BrokerHost(AgentClient client, FileCache cache, HookIpcServer pipe)
    {
        Client = client;
        Cache = cache;
        Pipe = pipe;
    }

    public AgentClient Client { get; }

    public FileCache Cache { get; }

    public HookIpcServer Pipe { get; }

    public static BrokerHost Start(BrokerOptions options)
    {
        var client = new AgentClient(new TcpConnectionFactory(options.AgentHost, options.AgentPort, options.Transport), options.ShareName, options.Transport);
        var cache = new FileCache(client, options.GetCacheRoot(), options.MetadataTtl);
        var pipe = new HookIpcServer(HookIpcProtocol.GetPipeName(options.SessionId), new HookRequestHandler(cache, options.Mappings));
        pipe.Start();
        return new BrokerHost(client, cache, pipe);
    }

    public async ValueTask DisposeAsync()
    {
        await Pipe.DisposeAsync().ConfigureAwait(false);
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
