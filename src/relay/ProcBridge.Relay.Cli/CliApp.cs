using ProcBridge.Relay.Cli.Interfaces;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.ViewModels.Accounts;
using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Shares;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Cli;

/// <summary>
/// Management CLI of the relay (decision 15.3: REST + CLI, no web UI). Exit codes: 0 ok, 1 relay refused, 2 usage.
/// </summary>
public sealed class CliApp
{
    public const string UrlEnvironmentVariable = "PROCBRIDGE_RELAY_URL";
    public const string TokenEnvironmentVariable = "PROCBRIDGE_RELAY_TOKEN";
    public const string PasswordEnvironmentVariable = "PROCBRIDGE_RELAY_PASSWORD";

    private const string Usage = """
        procbridge-relay [--url <relay>] [--token <token>] <command>

          register <user> [--password <p>]       create an account
          login <user> [--password <p>]          sign in, remember url + session token
          logout                                 end the session, forget the token
          whoami
          device register <name> [--public-key <base64>]   prints the device token ONCE
          device list
          device revoke <deviceId>
          device shares                          (device token) shares this device may use
          share create <deviceId> <name>
          share list
          share delete <shareId>
          share grant <shareId> <deviceId>       allow a device to use the share
          share grants <shareId>
          share revoke <shareId> <deviceId>
          tunnel open <shareId>                  (device token) open a session, print ticket

        --url / PROCBRIDGE_RELAY_URL, --token / PROCBRIDGE_RELAY_TOKEN, --password / PROCBRIDGE_RELAY_PASSWORD.
        """;

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly CliConfigStore _config;
    private readonly IPasswordReader _passwords;
    private readonly Func<Uri, IRelayApi> _apiFactory;

    public CliApp(TextWriter output, TextWriter error, CliConfigStore config, IPasswordReader passwords, Func<Uri, IRelayApi> apiFactory)
    {
        _out = output;
        _err = error;
        _config = config;
        _passwords = passwords;
        _apiFactory = apiFactory;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var arguments = new CliArguments(args);
        if (arguments.Positionals.Count == 0 || arguments.HasOption("help"))
        {
            _out.WriteLine(Usage);
            return arguments.Positionals.Count == 0 && !arguments.HasOption("help") ? 2 : 0;
        }
        try
        {
            await DispatchAsync(arguments, cancellationToken);
            return 0;
        }
        catch (CliUsageException ex)
        {
            _err.WriteLine(ex.Message);
            _err.WriteLine(Usage);
            return 2;
        }
        catch (RelayApiException ex)
        {
            _err.WriteLine($"Relay từ chối ({ex.StatusCode} {ex.Code}): {ex.Message}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            _err.WriteLine($"Không kết nối được relay: {ex.Message}");
            return 1;
        }
    }

    private Task DispatchAsync(CliArguments a, CancellationToken ct)
    {
        var command = a.Positionals[0].ToLowerInvariant();
        var sub = a.Positionals.Count > 1 ? a.Positionals[1].ToLowerInvariant() : string.Empty;
        return (command, sub) switch
        {
            ("register", _) => RegisterAsync(a, ct),
            ("login", _) => LoginAsync(a, ct),
            ("logout", _) => LogoutAsync(a, ct),
            ("whoami", _) => WhoAmIAsync(a, ct),
            ("device", "register") => DeviceRegisterAsync(a, ct),
            ("device", "list") => DeviceListAsync(a, ct),
            ("device", "revoke") => DeviceRevokeAsync(a, ct),
            ("device", "shares") => DeviceSharesAsync(a, ct),
            ("share", "create") => ShareCreateAsync(a, ct),
            ("share", "list") => ShareListAsync(a, ct),
            ("share", "delete") => ShareDeleteAsync(a, ct),
            ("share", "grant") => ShareGrantAsync(a, ct),
            ("share", "grants") => ShareGrantsAsync(a, ct),
            ("share", "revoke") => ShareRevokeAsync(a, ct),
            ("tunnel", "open") => TunnelOpenAsync(a, ct),
            _ => throw new CliUsageException($"Lệnh không hợp lệ: {string.Join(' ', a.Positionals)}"),
        };
    }

    private async Task RegisterAsync(CliArguments a, CancellationToken ct)
    {
        var userName = a.Required(1, "tên đăng nhập");
        var account = await Api(a, authenticated: false).Accounts.RegisterAsync(
            new RegisterAccountRequestVM { UserName = userName, Password = Password(a) }, ct);
        _out.WriteLine($"Đã tạo tài khoản {account.UserName} ({account.Id}).");
    }

    private async Task LoginAsync(CliArguments a, CancellationToken ct)
    {
        var userName = a.Required(1, "tên đăng nhập");
        var url = ResolveUrl(a);
        var result = await _apiFactory(url).Accounts.LoginAsync(new LoginRequestVM { UserName = userName, Password = Password(a) }, ct);
        _config.Save(new CliConfig { Url = url.ToString(), Token = result.Token });
        _out.WriteLine($"Đã đăng nhập {result.Account.UserName}, phiên hết hạn {result.ExpiresAt:u}.");
    }

    private async Task LogoutAsync(CliArguments a, CancellationToken ct)
    {
        await Api(a).Accounts.LogoutAsync(ct);
        var config = _config.Load();
        config.Token = null;
        _config.Save(config);
        _out.WriteLine("Đã đăng xuất.");
    }

    private async Task WhoAmIAsync(CliArguments a, CancellationToken ct)
    {
        var account = await Api(a).Accounts.GetMeAsync(ct);
        _out.WriteLine($"{account.UserName} ({account.Id})");
    }

    private async Task DeviceRegisterAsync(CliArguments a, CancellationToken ct)
    {
        var name = a.Required(2, "tên máy");
        var result = await Api(a).Devices.RegisterAsync(new RegisterDeviceRequestVM { Name = name, PublicKey = a.Option("public-key") }, ct);
        _out.WriteLine($"deviceId: {result.Device.Id}");
        _out.WriteLine($"token: {result.Token}");
        _out.WriteLine("Token chỉ hiện một lần; cất vào cấu hình Agent/Broker của máy đó.");
    }

    private async Task DeviceListAsync(CliArguments a, CancellationToken ct)
    {
        foreach (var d in await Api(a).Devices.GetAllAsync(ct))
            _out.WriteLine($"{d.Id}  {d.Name}  {(d.IsRevoked ? "revoked" : d.IsOnline ? "online" : "offline")}");
    }

    private async Task DeviceRevokeAsync(CliArguments a, CancellationToken ct)
    {
        var deviceId = a.RequiredGuid(2, "deviceId");
        await Api(a).Devices.RevokeAsync(deviceId, ct);
        _out.WriteLine($"Đã thu hồi máy {deviceId}.");
    }

    private async Task DeviceSharesAsync(CliArguments a, CancellationToken ct)
    {
        foreach (var s in await Api(a).Devices.GetAccessibleSharesAsync(ct))
            _out.WriteLine($"{s.ShareId}  {s.Name}  host={s.HostDeviceName} ({s.HostDeviceId})  {(s.IsHostOnline ? "online" : "offline")}");
    }

    private async Task ShareCreateAsync(CliArguments a, CancellationToken ct)
    {
        var deviceId = a.RequiredGuid(2, "deviceId");
        var name = a.Required(3, "tên chia sẻ");
        var share = await Api(a).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = deviceId, Name = name }, ct);
        _out.WriteLine($"shareId: {share.Id}");
    }

    private async Task ShareListAsync(CliArguments a, CancellationToken ct)
    {
        foreach (var s in await Api(a).Shares.GetAllAsync(ct))
            _out.WriteLine($"{s.Id}  {s.Name}  device={s.DeviceId}");
    }

    private async Task ShareDeleteAsync(CliArguments a, CancellationToken ct)
    {
        var shareId = a.RequiredGuid(2, "shareId");
        await Api(a).Shares.DeleteAsync(shareId, ct);
        _out.WriteLine($"Đã xoá chia sẻ {shareId}.");
    }

    private async Task ShareGrantAsync(CliArguments a, CancellationToken ct)
    {
        var shareId = a.RequiredGuid(2, "shareId");
        var deviceId = a.RequiredGuid(3, "deviceId");
        await Api(a).Shares.GrantAsync(shareId, new GrantShareRequestVM { DeviceId = deviceId }, ct);
        _out.WriteLine($"Đã cho máy {deviceId} dùng chia sẻ {shareId}.");
    }

    private async Task ShareGrantsAsync(CliArguments a, CancellationToken ct)
    {
        foreach (var g in await Api(a).Shares.GetGrantsAsync(a.RequiredGuid(2, "shareId"), ct))
            _out.WriteLine($"{g.DeviceId}  {g.CreatedAt:u}");
    }

    private async Task ShareRevokeAsync(CliArguments a, CancellationToken ct)
    {
        var shareId = a.RequiredGuid(2, "shareId");
        var deviceId = a.RequiredGuid(3, "deviceId");
        await Api(a).Shares.RevokeGrantAsync(shareId, deviceId, ct);
        _out.WriteLine($"Đã thu quyền của máy {deviceId} trên chia sẻ {shareId}.");
    }

    private async Task TunnelOpenAsync(CliArguments a, CancellationToken ct)
    {
        var session = await Api(a).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = a.RequiredGuid(2, "shareId") }, ct);
        _out.WriteLine($"sessionId: {session.SessionId}");
        _out.WriteLine($"ticket: {session.Ticket}");
        _out.WriteLine($"expiresAt: {session.ExpiresAt:u}");
    }

    private IRelayApi Api(CliArguments a, bool authenticated = true)
    {
        var api = _apiFactory(ResolveUrl(a));
        if (authenticated)
        {
            var token = a.Option("token") ?? Environment.GetEnvironmentVariable(TokenEnvironmentVariable) ?? _config.Load().Token;
            if (string.IsNullOrWhiteSpace(token))
                throw new CliUsageException("Chưa đăng nhập: chạy `login` hoặc truyền --token.");
            api.SetBearerToken(token);
        }
        return api;
    }

    private Uri ResolveUrl(CliArguments a)
    {
        var url = a.Option("url") ?? Environment.GetEnvironmentVariable(UrlEnvironmentVariable) ?? _config.Load().Url;
        if (string.IsNullOrWhiteSpace(url))
            throw new CliUsageException("Thiếu địa chỉ relay: --url hoặc PROCBRIDGE_RELAY_URL.");
        if (!url.EndsWith('/'))
            url += "/";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : throw new CliUsageException($"Địa chỉ relay không hợp lệ: {url}");
    }

    private string Password(CliArguments a)
        => a.Option("password") ?? Environment.GetEnvironmentVariable(PasswordEnvironmentVariable) ?? _passwords.Read("Mật khẩu: ");
}
