using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ProcBridge.Agent.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Agent;

/// <summary>
/// TCP listener of the Agent: accepts connections from Brokers and runs one <see cref="AgentSession"/> per
/// connection. M2 has no authentication or encryption yet (M5): only run it on a trusted LAN.
/// </summary>
public sealed class AgentServer : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, ReadOnlyShare> _shares;
    private readonly AgentOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<AgentSession, Task> _sessions = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    public AgentServer(IEnumerable<ReadOnlyShare> shares, AgentOptions options)
    {
        _shares = shares.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        _options = options;
        _listener = new TcpListener(options.BindAddress, options.Port);
    }

    /// <summary>Port actually listened on (useful when <see cref="AgentOptions.Port"/> is 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ActiveSessionCount => _sessions.Count;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            client.NoDelay = true;
            var channel = new StreamFrameChannel(client.GetStream(), ownsStream: true, _options.MaxFrameSize);
            var session = new AgentSession(channel, client, _shares, _options);

            // The session removes itself when it ends; it must not start before it is registered, or a connection
            // that fails at once would be removed first and then re-added for good.
            var registered = new TaskCompletionSource();
            _sessions[session] = RunSessionAsync(session, registered.Task);
            registered.SetResult();
        }
    }

    private async Task RunSessionAsync(AgentSession session, Task registered)
    {
        await registered.ConfigureAwait(false);
        try
        {
            await session.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A broken client must not stop the server; the session already closed its socket.
        }
        finally
        {
            _sessions.TryRemove(session, out _);
        }
    }

    /// <summary>Stops listening and closes every open connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (AgentSession session in _sessions.Keys)
            session.Abort();
        await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
