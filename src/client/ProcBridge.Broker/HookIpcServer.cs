using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using ProcBridge.HookIpc;

namespace ProcBridge.Broker;

/// <summary>
/// Named pipe endpoint for Hook.dll (plan section 6): message mode, many instances, current user only. Each
/// connected instance is served by its own loop (request, reply, repeat), so hook threads using separate
/// instances run in parallel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HookIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly HookRequestHandler _handler;
    private readonly Func<bool, NamedPipeServerStream> _createInstance;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clients = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    /// <param name="pipeName">Name without the <c>\\.\pipe\</c> prefix, see <see cref="HookIpcProtocol.GetPipeName"/>.</param>
    /// <param name="createInstance">Creates a pipe instance (argument: is it the first one); tests inject failures here.</param>
    public HookIpcServer(string pipeName, HookRequestHandler handler, Func<bool, NamedPipeServerStream>? createInstance = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _createInstance = createInstance ?? (first => CreateDefaultInstance(pipeName, first));
    }

    public string PipeName => _pipeName;

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
            next = null;
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
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

                int size = await _handler.HandleAsync(request.AsMemory(0, length), reply, _stopping.Token).ConfigureAwait(false);
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
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
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
        _stopping.Dispose();
    }
}
