using System.Diagnostics;
using System.IO.Pipes;
using ProcBridge.Broker;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// IPC timeout for large files: a request the Broker works on for longer than <c>PROCBRIDGE_IPC_TIMEOUT_MS</c> (a large
/// download) still succeeds while the Broker sends progress messages; the timeout counts from the last message.
/// </summary>
[TestClass]
public sealed class BrokerProgressTests
{
    private const string Content = "downloaded slowly";

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task SlowRequestWithProgress_OutlastsTheCallTimeout(string platform)
    {
        using HookSandbox sandbox = new(platform);
        File.WriteAllText(Path.Combine(sandbox.RealRoot, "a.txt"), Content);
        string sessionId = HookIpcProtocol.NewSessionId();
        using var broker = new StandInBroker(HookIpcProtocol.GetPipeName(sessionId), sandbox);

        var startInfo = new ProcessStartInfo(sandbox.Binaries.Spawn,
            $"--broker {sessionId} {Environment.ProcessId} -- cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Opening a.txt takes 12 s, the timeout is 8 s: only the progress messages (every 250 ms) keep the call alive. The
        // margins are wide because a busy test run can stall this process for seconds.
        startInfo.Environment["PROCBRIDGE_IPC_TIMEOUT_MS"] = "8000";
        using Process process = Process.Start(startInfo)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        bool exited = true;
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                exited = false;
                process.Kill(entireProcessTree: true);
            }
        }

        Assert.IsTrue(exited, "the hooked call hung");
        Assert.AreEqual(0, process.ExitCode, await output + await error);
        Assert.Contains(Content, await output);
        Assert.IsGreaterThan(0, broker.ProgressSent);
    }

    /// <summary>
    /// A Broker stand-in: answers from the real folder of the sandbox at once, except opening a file (Resolve of a file:
    /// the download of a large file), which takes 12 s with a progress message every 250 ms. Runs on threads of its own with
    /// blocking I/O: under a busy test run the thread pool may stall for seconds, which would delay the progress
    /// messages past the call timeout and test the machine instead of the protocol.
    /// </summary>
    private sealed class StandInBroker : IDisposable
    {
        private readonly string _pipeName;
        private readonly HookSandbox _sandbox;
        private readonly Thread _acceptThread;
        private readonly List<Thread> _clients = [];
        private volatile bool _stopping;
        private volatile NamedPipeServerStream? _waiting;
        private int _progressSent;

        public StandInBroker(string pipeName, HookSandbox sandbox)
        {
            _pipeName = pipeName;
            _sandbox = sandbox;
            // Created here so the hook finds the pipe as soon as the process starts.
            _waiting = HookIpcServer.CreateDefaultInstance(pipeName, true);
            _acceptThread = new Thread(Accept) { IsBackground = true, Name = "stand-in broker" };
            _acceptThread.Start();
        }

        public int ProgressSent => Volatile.Read(ref _progressSent);

        private void Accept()
        {
            while (!_stopping)
            {
                NamedPipeServerStream pipe = _waiting ?? HookIpcServer.CreateDefaultInstance(_pipeName, false);
                _waiting = pipe;
                try
                {
                    pipe.WaitForConnection();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    pipe.Dispose();
                    _waiting = null;
                    continue;
                }
                _waiting = null;
                var client = new Thread(() => Answer(pipe)) { IsBackground = true };
                lock (_clients)
                    _clients.Add(client);
                client.Start();
            }
        }

        private void Answer(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                byte[] buffer = new byte[HookIpcProtocol.MaxMessageSize];
                try
                {
                    int length;
                    while (!_stopping && (length = pipe.Read(buffer)) > 0)
                    {
                        int size = Reply(pipe, buffer, length);
                        pipe.Write(buffer.AsSpan(0, size));
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            }
        }

        private int Reply(NamedPipeServerStream pipe, byte[] buffer, int length)
        {
            HookIpcHeader header = HookIpcCodec.ReadHeader(buffer.AsSpan(0, length));
            switch (header.MessageType)
            {
                case HookIpcMessageType.Hello:
                    return HookIpcCodec.Encode(buffer, header.RequestId, new HookHelloReply { VirtualRoots = [_sandbox.VirtualRoot] });
                case HookIpcMessageType.List:
                {
                    HookListRequest list = ReadList(buffer.AsSpan(0, length));
                    HookListEntry[] entries = list.StartIndex == 0 && list.RelativePath.Length == 0
                        ? [new HookListEntry(new HookFileInfo { Attributes = 0x80, Size = Content.Length, AllocationSize = 4096 }, "a.txt")]
                        : [];
                    return HookIpcCodec.Encode(buffer, header.RequestId, new HookListReply { Entries = entries, HasMore = false });
                }
                case HookIpcMessageType.Resolve:
                case HookIpcMessageType.Stat:
                {
                    string relative = header.MessageType == HookIpcMessageType.Resolve
                        ? ReadResolvePath(buffer.AsSpan(0, length))
                        : ReadStatPath(buffer.AsSpan(0, length));
                    string real = Path.Combine(_sandbox.RealRoot, relative);
                    if (header.MessageType == HookIpcMessageType.Resolve && File.Exists(real))
                        SendProgress(pipe, header);
                    if (!Path.Exists(real))
                        return HookIpcCodec.EncodeError(buffer, header.MessageType, header.RequestId, 0xC0000034); // STATUS_OBJECT_NAME_NOT_FOUND
                    HookFileInfo info = File.Exists(real)
                        ? new HookFileInfo { Attributes = 0x80, Size = Content.Length, AllocationSize = 4096 }
                        : new HookFileInfo { Attributes = 0x10 };
                    return header.MessageType == HookIpcMessageType.Resolve
                        ? HookIpcCodec.Encode(buffer, header.RequestId, new HookResolveReply { Info = info, CachePath = real })
                        : HookIpcCodec.Encode(buffer, header.RequestId, new HookStatReply { Info = info });
                }
                default:
                    return HookIpcCodec.EncodeError(buffer, header.MessageType, header.RequestId, 0xC00000BB); // STATUS_NOT_SUPPORTED
            }
        }

        /// <summary>12 s of work: a progress message every 250 ms, as the Broker sends them during a large download.</summary>
        private void SendProgress(NamedPipeServerStream pipe, HookIpcHeader header)
        {
            byte[] progress = new byte[HookIpcProtocol.HeaderSize];
            int size = HookIpcCodec.EncodeError(progress, header.MessageType, header.RequestId, HookIpcProtocol.StatusInProgress);
            for (int i = 0; i < 48 && !_stopping; i++)
            {
                Thread.Sleep(250);
                pipe.Write(progress.AsSpan(0, size));
                Interlocked.Increment(ref _progressSent);
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _waiting?.Dispose();
            _acceptThread.Join(TimeSpan.FromSeconds(10));
            Thread[] clients;
            lock (_clients)
                clients = [.. _clients];
            foreach (Thread client in clients)
                client.Join(TimeSpan.FromSeconds(10));
        }
    }

    private static HookListRequest ReadList(ReadOnlySpan<byte> message)
    {
        var reader = new HookIpcReader(message);
        return HookListRequest.Read(ref reader);
    }

    private static string ReadResolvePath(ReadOnlySpan<byte> message)
    {
        var reader = new HookIpcReader(message);
        return HookResolveRequest.Read(ref reader).RelativePath;
    }

    private static string ReadStatPath(ReadOnlySpan<byte> message)
    {
        var reader = new HookIpcReader(message);
        return HookStatRequest.Read(ref reader).RelativePath;
    }
}
