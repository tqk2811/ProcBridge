using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;

namespace ProcBridge.Broker;

/// <summary>
/// Named pipe endpoint for Hook.dll (plan section 6): message mode, many instances, current user only. Each
/// connected instance is served by its own loop (request, reply, repeat), so hook threads using separate
/// instances run in parallel. Owns the handler it serves and disposes it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HookIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly HookRequestHandler _handler;
    private readonly Func<bool, NamedPipeServerStream> _createInstance;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clients = new();
    private int _peakClientCount;

    /// <summary>Most pipe connections open at the same time so far; tests check that watches share one instance.</summary>
    internal int PeakClientCount => Volatile.Read(ref _peakClientCount);
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;
    private long _anonymousClients;

    /// <param name="pipeName">Name without the <c>\\.\pipe\</c> prefix, see <see cref="HookIpcProtocol.GetPipeName"/>.</param>
    /// <param name="createInstance">Creates a pipe instance (argument: is it the first one); tests inject failures here.</param>
    public HookIpcServer(string pipeName, HookRequestHandler handler, Func<bool, NamedPipeServerStream>? createInstance = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _createInstance = createInstance ?? (first => CreateDefaultInstance(pipeName, first));
    }

    public string PipeName => _pipeName;

    public HookRequestHandler Handler => _handler;

    public void Start()
    {
        // Created here so a client connecting right after Start finds the pipe. As the first instance it fails
        // when something else already owns the name, instead of silently sharing it with that process.
        NamedPipeServerStream first = _createInstance(true);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first));
    }

    /// <summary>The production pipe: message mode, many instances, current user only; <paramref name="first"/> claims the name.</summary>
    public static NamedPipeServerStream CreateDefaultInstance(string pipeName, bool first) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Message,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        HookIpcProtocol.MaxMessageSize,
        HookIpcProtocol.MaxMessageSize);

    /// <summary>Creates the next instance, retrying with growing delays; null when the server is stopping.</summary>
    private async Task<NamedPipeServerStream?> CreateNextInstanceAsync()
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(100);
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                return _createInstance(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"ProcBridge: cannot create pipe instance '{_pipeName}': {e.Message}; retrying in {delay.TotalMilliseconds:0} ms.");
            }

            try
            {
                await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }
        return null;
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream? next)
    {
        while (true)
        {
            next ??= await CreateNextInstanceAsync().ConfigureAwait(false);
            if (next is null)
                return;

            try
            {
                await next.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                await next.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // Client vanished during connect; reuse a fresh instance.
                await next.DisposeAsync().ConfigureAwait(false);
                next = null;
                continue;
            }

            NamedPipeServerStream connected = next;
            _clients[connected] = Task.Run(() => ServeAsync(connected));
            int count = _clients.Count;
            for (int peak = Volatile.Read(ref _peakClientCount); count > peak; peak = Volatile.Read(ref _peakClientCount))
                Interlocked.CompareExchange(ref _peakClientCount, count, peak);
            next = null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>
    /// The client's process id: it owns the directory watches its connections register. When the system will not
    /// tell, the connection gets an id of its own (negative, so it never collides with a process id).
    /// </summary>
    private long GetClientId(NamedPipeServerStream pipe)
    {
        try
        {
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
                return processId;
        }
        catch (ObjectDisposedException)
        {
        }
        return -Interlocked.Increment(ref _anonymousClients);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        long clientId = GetClientId(pipe);
        _handler.ClientConnected(clientId);
        byte[] request = new byte[HookIpcProtocol.MaxMessageSize];
        byte[] reply = new byte[HookIpcProtocol.MaxMessageSize];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int length = 0;
                do
                {
                    if (length == request.Length)
                        return; // Oversized message: drop the client rather than guess.
                    int n = await pipe.ReadAsync(request.AsMemory(length), _stopping.Token).ConfigureAwait(false);
                    if (n == 0)
                        return;
                    length += n;
                }
                while (!pipe.IsMessageComplete);

                int size = IsWaitChanges(request.AsSpan(0, length))
                    ? await HandleWhileWatchingPipeAsync(pipe, clientId, request.AsMemory(0, length), reply).ConfigureAwait(false)
                    : await _handler.HandleAsync(clientId, request.AsMemory(0, length), reply, _stopping.Token).ConfigureAwait(false);
                if (size < 0)
                    return; // The client went away while its request was being handled.
                await pipe.WriteAsync(reply.AsMemory(0, size), _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client disconnected or server stopping.
        }
        finally
        {
            _clients.TryRemove(pipe, out _);
            _handler.ClientDisconnected(clientId);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsWaitChanges(ReadOnlySpan<byte> message)
        => message.Length >= HookIpcProtocol.HeaderSize
            && (HookIpcMessageType)BitConverter.ToUInt16(message[6..8]) is HookIpcMessageType.WaitChanges or HookIpcMessageType.WaitAnyChanges;

    /// <summary>
    /// Handles a request that can block for a long time (WaitChanges long poll) while still reading the pipe: the client
    /// sends nothing until it gets the reply, so a read that completes means it closed the pipe (or broke the protocol)
    /// and the wait is cancelled. Returns -1 in that case.
    /// </summary>
    private async Task<int> HandleWhileWatchingPipeAsync(NamedPipeServerStream pipe, long clientId, ReadOnlyMemory<byte> request, Memory<byte> reply)
    {
        using var handling = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        using var probing = new CancellationTokenSource();
        Task<int> probe = pipe.ReadAsync(new byte[1], probing.Token).AsTask();
        Task<int> handle = _handler.HandleAsync(clientId, request, reply, handling.Token);
        if (await Task.WhenAny(handle, probe).ConfigureAwait(false) == probe)
        {
            handling.Cancel();
            await handle.ConfigureAwait(false);
            return -1;
        }

        probing.Cancel();
        try
        {
            if (await probe.ConfigureAwait(false) != 0)
                return -1; // Unsolicited data while a request is pending.
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        return await handle.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        await _acceptLoop.ConfigureAwait(false);
        foreach (NamedPipeServerStream pipe in _clients.Keys)
            await pipe.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _handler.Dispose();
        _stopping.Dispose();
    }
}
