using System.Net.Sockets;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Protocol.Transport;

/// <summary>Opens plain TCP connections to an Agent on the LAN (plan 8.1).</summary>
public sealed class TcpConnectionFactory : IConnectionFactory
{
    private readonly string _host;
    private readonly int _port;
    private readonly TransportOptions _options;
    private readonly Func<IFrameProtector?>? _protectorFactory;

    /// <param name="protectorFactory">Creates the per-connection encryption layer (M5); null keeps frames plain.</param>
    public TcpConnectionFactory(string host, int port, TransportOptions options, Func<IFrameProtector?>? protectorFactory = null)
    {
        _host = host;
        _port = port;
        _options = options;
        _protectorFactory = protectorFactory;
    }

    public string Description => $"tcp://{_host}:{_port}";

    public async ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
            return new StreamFrameChannel(new OwnedNetworkStream(client), ownsStream: true, _options.MaxFrameSize, _protectorFactory?.Invoke());
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>NetworkStream that also disposes its TcpClient.</summary>
    private sealed class OwnedNetworkStream(TcpClient client) : NetworkStream(client.Client, ownsSocket: true)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                client.Dispose();
        }
    }
}
