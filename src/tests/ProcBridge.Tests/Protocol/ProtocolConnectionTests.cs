using System.Net;
using System.Net.Sockets;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Protocol;

[TestClass]
public sealed class ProtocolConnectionTests
{
    private static readonly HelloRequest Hello = new() { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = "s" };

    [TestMethod]
    public async Task Hello_NeverAnswered_TimesOut()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Accepts and then stays silent, like a hung Agent.
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            var options = new TransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(500), RequestTimeout = TimeSpan.FromMilliseconds(500) };
            var factory = new TcpConnectionFactory("127.0.0.1", port, options);

            var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => ProtocolConnection.OpenAsync(factory, Hello, options, CancellationToken.None));
            Assert.AreEqual(NtStatus.IoTimeout, e.Status);
            (await accept).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task Connect_NoListener_IsNetworkUnreachable()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var options = new TransportOptions { ConnectTimeout = TimeSpan.FromSeconds(5) };
        var factory = new TcpConnectionFactory("127.0.0.1", port, options);
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => ProtocolConnection.OpenAsync(factory, Hello, options, CancellationToken.None));
        Assert.IsTrue(e.IsConnectionFailure);
        Assert.AreEqual(NtStatus.NetworkUnreachable, e.Status);
    }
}
