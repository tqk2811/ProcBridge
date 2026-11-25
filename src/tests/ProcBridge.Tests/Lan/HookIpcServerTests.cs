using System.IO.Pipes;
using ProcBridge.Broker;
using ProcBridge.HookIpc;

namespace ProcBridge.Tests.Lan;

[TestClass]
public sealed class HookIpcServerTests
{
    private static HookRequestHandler CreateHandler(TempFolder cacheDir)
    {
        // The handler is never reached: these tests only open and close pipe connections.
        var client = new AgentClient(new ProcBridge.Protocol.Transport.TcpConnectionFactory("127.0.0.1", 1, new ProcBridge.Protocol.Models.TransportOptions()), "src", new ProcBridge.Protocol.Models.TransportOptions());
        return new HookRequestHandler(new FileCache(client, cacheDir.Path, TimeSpan.Zero), []);
    }

    private static async Task ConnectAsync(string pipeName)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
    }

    [TestMethod]
    public async Task SecondServerOnTheSamePipeName_IsRefused()
    {
        using var cacheDir = new TempFolder("cache");
        string name = HookIpcProtocol.GetPipeName(HookIpcProtocol.NewSessionId());
        await using var first = new HookIpcServer(name, CreateHandler(cacheDir));
        first.Start();
        await using var squatter = new HookIpcServer(name, CreateHandler(cacheDir));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => squatter.Start());
    }

    [TestMethod]
    public void SessionIds_AreRandomHex()
    {
        string[] ids = Enumerable.Range(0, 50).Select(_ => HookIpcProtocol.NewSessionId()).ToArray();
        Assert.HasCount(50, ids.Distinct());
        foreach (string id in ids)
            Assert.AreEqual(32, id.Length);
        Assert.IsTrue(ids.All(x => x.All(Uri.IsHexDigit)));
    }

    [TestMethod]
    public async Task FailingInstanceCreation_IsRetriedInsteadOfEndingTheAcceptLoop()
    {
        using var cacheDir = new TempFolder("cache");
        string name = HookIpcProtocol.GetPipeName(HookIpcProtocol.NewSessionId());
        int created = 0;
        int failures = 0;
        NamedPipeServerStream Create(bool first)
        {
            // The first instance and the one after the first client succeed; the next two creations fail.
            int n = Interlocked.Increment(ref created);
            if (n is 3 or 4)
            {
                Interlocked.Increment(ref failures);
                throw new IOException("All pipe instances are busy.");
            }
            return HookIpcServer.CreateDefaultInstance(name, first);
        }

        await using var server = new HookIpcServer(name, CreateHandler(cacheDir), Create);
        server.Start();
        await ConnectAsync(name);
        await ConnectAsync(name);
        await ConnectAsync(name);
        Assert.AreEqual(2, Volatile.Read(ref failures));
    }
}
