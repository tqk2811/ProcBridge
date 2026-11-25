using System.IO.Pipes;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Interfaces;
using ProcBridge.HookIpc.Models;

namespace ProcBridge.Tests.Lan;

/// <summary>Minimal stand-in for the C++ pipe client of Hook.dll.</summary>
internal sealed class FakeHook(NamedPipeClientStream pipe) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[HookIpcProtocol.MaxMessageSize];
    private uint _nextId;

    public static async Task<FakeHook> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        pipe.ReadMode = PipeTransmissionMode.Message;
        return new FakeHook(pipe);
    }

    public async Task<(uint Status, byte[] Reply)> CallAsync(IHookIpcMessage request)
    {
        uint id = ++_nextId;
        int size = HookIpcCodec.Encode(_buffer, id, request);
        await pipe.WriteAsync(_buffer.AsMemory(0, size));
        int length = 0;
        do
        {
            length += await pipe.ReadAsync(_buffer.AsMemory(length));
        }
        while (!pipe.IsMessageComplete);
        byte[] reply = _buffer[..length];
        HookIpcHeader header = HookIpcCodec.ReadHeader(reply);
        Assert.AreEqual(id, header.RequestId);
        Assert.AreEqual(request.MessageType, header.MessageType);
        return (header.Status, reply);
    }

    public async Task<T> CallOkAsync<T>(IHookIpcMessage request, HookBodyReader<T> read)
    {
        (uint status, byte[] reply) = await CallAsync(request);
        Assert.AreEqual(0u, status, $"status 0x{status:X8}");
        var reader = new HookIpcReader(reply);
        return read(ref reader);
    }

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
