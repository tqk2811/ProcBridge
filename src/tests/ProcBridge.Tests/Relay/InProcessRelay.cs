using System.Net.WebSockets;
using Microsoft.AspNetCore.TestHost;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.ViewModels.Accounts;
using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Shares;
using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// The real relay host (REST + WebSocket tunnel) in this test process. On PostgreSQL when
/// <see cref="PostgresTestDatabase.EnvironmentVariable"/> is set (throw-away database, real EF repositories and
/// migrations), otherwise on the in-memory repositories of ProcBridge.Relay.Testing. WebSockets go through
/// <see cref="Sockets"/>, which also records every byte that crosses the relay on data lanes.
/// </summary>
internal sealed class InProcessRelay : IAsyncDisposable
{
    private readonly PostgresTestDatabase? _database;

    private InProcessRelay(PostgresTestDatabase? database)
    {
        _database = database;
        Factory = new RelayApiFactory(database?.ConnectionString);
        Sockets = new RecordingSocketFactory(Factory.Server);
    }

    public RelayApiFactory Factory { get; }

    public RecordingSocketFactory Sockets { get; }

    /// <summary>True when the relay runs on real PostgreSQL.</summary>
    public bool UsesPostgres => _database is not null;

    public static InProcessRelay Start()
        => new(PostgresTestDatabase.AdminConnectionString is null ? null : PostgresTestDatabase.Create());

    public RelayEndpoint Endpoint(string deviceToken) => new(Factory.Server.BaseAddress, deviceToken);

    public IRelayApi Api(string? token = null)
    {
        var api = new RelayApiImplement(Factory.CreateClient());
        api.SetBearerToken(token);
        return api;
    }

    /// <summary>One account owning a host device (machine B) and a client device (machine A) granted one share.</summary>
    public async Task<RelayTopology> CreateTopologyAsync(string shareName)
    {
        string user = "u" + Guid.NewGuid().ToString("N")[..10];
        IRelayApi anonymous = Api();
        await anonymous.Accounts.RegisterAsync(new RegisterAccountRequestVM { UserName = user, Password = "matkhau-dai-du" });
        string owner = (await anonymous.Accounts.LoginAsync(new LoginRequestVM { UserName = user, Password = "matkhau-dai-du" })).Token;
        IRelayApi api = Api(owner);
        DeviceRegisteredVM host = await api.Devices.RegisterAsync(new RegisterDeviceRequestVM { Name = "may-b" });
        DeviceRegisteredVM client = await api.Devices.RegisterAsync(new RegisterDeviceRequestVM { Name = "may-a" });
        ShareVM share = await api.Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = shareName });
        await api.Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = client.Device.Id });
        return new RelayTopology(owner, host, client, share.Id);
    }

    /// <summary>Waits until the relay lists <paramref name="device"/> online (its control channel is up).</summary>
    public async Task WaitOnlineAsync(string ownerToken, Guid device)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!(await Api(ownerToken).Devices.GetAllAsync()).Single(x => x.Id == device).IsOnline)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The Agent did not come online on the relay.");
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_database is not null)
            await _database.DisposeAsync();
    }
}

internal sealed record RelayTopology(string OwnerToken, DeviceRegisteredVM Host, DeviceRegisteredVM Client, Guid ShareId);

/// <summary>
/// WebSockets to the in-process test server. Data lanes (no bearer token) are wrapped so every byte sent or received
/// on them is copied to <see cref="CapturedLaneBytes"/>: that is exactly what the relay forwards.
/// </summary>
internal sealed class RecordingSocketFactory(TestServer server) : IRelaySocketFactory
{
    private readonly object _lock = new();
    private readonly MemoryStream _captured = new();

    public int LaneCount { get; private set; }

    public byte[] CapturedLaneBytes
    {
        get
        {
            lock (_lock)
                return _captured.ToArray();
        }
    }

    public async Task<WebSocket> ConnectAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken)
    {
        WebSocketClient client = server.CreateWebSocketClient();
        if (bearerToken is not null)
            client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + bearerToken;
        WebSocket socket = await client.ConnectAsync(uri, cancellationToken);
        if (bearerToken is not null)
            return socket;
        lock (_lock)
            LaneCount++;
        return new RecordingWebSocket(socket, Capture);
    }

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
            _captured.Write(bytes);
    }

    private sealed class RecordingWebSocket(WebSocket inner, SpanAction capture) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;

        public override void Abort() => inner.Abort();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => inner.Dispose();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            WebSocketReceiveResult result = await inner.ReceiveAsync(buffer, cancellationToken);
            capture(buffer.AsSpan(0, result.Count));
            return result;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            capture(buffer.AsSpan());
            return inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }
    }

    private delegate void SpanAction(ReadOnlySpan<byte> bytes);
}
