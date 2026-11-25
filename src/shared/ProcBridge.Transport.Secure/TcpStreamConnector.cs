using System.Net.Sockets;
using ProcBridge.Transport.Secure.Interfaces;

namespace ProcBridge.Transport.Secure;

/// <summary>Opens TCP connections to an Agent on the LAN (plan 8.1).</summary>
public sealed class TcpStreamConnector : IStreamConnector
{
    private readonly string _host;
    private readonly int _port;

    public TcpStreamConnector(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public string Description => $"{_host}:{_port}";

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
            return new OwnedNetworkStream(client);
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
