using System.Diagnostics;
using System.IO.Pipes;
using ProcBridge.Broker;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Models;

namespace ProcBridge.Tests.Native;

/// <summary>Plan 4.6: a Broker that stops answering makes the hooked call fail with STATUS_IO_TIMEOUT, it does not hang.</summary>
[TestClass]
public sealed class BrokerTimeoutTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task SilentBroker_FailsTheCallWithTimeout(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string sessionId = HookIpcProtocol.NewSessionId();
        using var stop = new CancellationTokenSource();
        Task server = ServeAsync(HookIpcProtocol.GetPipeName(sessionId), sandbox, stop.Token);

        var startInfo = new ProcessStartInfo(sandbox.Binaries.Spawn,
            $"--broker {sessionId} {Environment.ProcessId} -- cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["PROCBRIDGE_IPC_TIMEOUT_MS"] = "1000";
        var watch = Stopwatch.StartNew();
        using Process process = Process.Start(startInfo)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        bool exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        if (!exited)
            process.Kill(entireProcessTree: true);
        stop.Cancel();
        await server;

        Assert.IsTrue(exited, "the hooked call hung");
        Assert.IsLessThan(TimeSpan.FromSeconds(30), watch.Elapsed);
        Assert.AreEqual(1, process.ExitCode, await output);
        // STATUS_IO_TIMEOUT becomes ERROR_SEM_TIMEOUT (121) in kernelbase.
        string message = new System.ComponentModel.Win32Exception(121).Message;
        Assert.Contains(message, await error);
    }

    /// <summary>
    /// A Broker stand-in: answers Hello and the resolve of the virtual root (so the hook starts), never answers
    /// anything else.
    /// </summary>
    private static async Task ServeAsync(string pipeName, HookSandbox sandbox, CancellationToken cancellationToken)
    {
        var clients = new List<Task>();
        try
        {
            bool first = true;
            while (true)
            {
                NamedPipeServerStream pipe = HookIpcServer.CreateDefaultInstance(pipeName, first);
                first = false;
                await pipe.WaitForConnectionAsync(cancellationToken);
                clients.Add(AnswerAsync(pipe, sandbox, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(clients);
    }

    private static async Task AnswerAsync(NamedPipeServerStream pipe, HookSandbox sandbox, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            byte[] buffer = new byte[HookIpcProtocol.MaxMessageSize];
            try
            {
                while (true)
                {
                    int length = await pipe.ReadAsync(buffer, cancellationToken);
                    if (length == 0)
                        return;
                    HookIpcHeader header = HookIpcCodec.ReadHeader(buffer.AsSpan(0, length));
                    int size;
                    if (header.MessageType == HookIpcMessageType.Hello)
                    {
                        size = HookIpcCodec.Encode(buffer, header.RequestId, new HookHelloReply { VirtualRoots = [sandbox.VirtualRoot] });
                    }
                    else if (header.MessageType == HookIpcMessageType.Resolve && ReadRelativePath(buffer.AsSpan(0, length)).Length == 0)
                    {
                        size = HookIpcCodec.Encode(buffer, header.RequestId, new HookResolveReply
                        {
                            Info = new HookFileInfo { Attributes = 0x10 },
                            CachePath = sandbox.RealRoot,
                        });
                    }
                    else
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        return;
                    }
                    await pipe.WriteAsync(buffer.AsMemory(0, size), cancellationToken);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
            }
        }
    }

    private static string ReadRelativePath(ReadOnlySpan<byte> message)
    {
        var reader = new HookIpcReader(message);
        return HookResolveRequest.Read(ref reader).RelativePath;
    }
}
