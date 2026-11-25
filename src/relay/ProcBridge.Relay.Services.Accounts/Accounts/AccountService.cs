using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services.Accounts.Interfaces;
using ProcBridge.Relay.Services.Accounts.Models;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Common.Security;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.ViewModels.Accounts;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Services.Accounts;

public sealed class AccountService : IAccountService
{
    private const int MinPasswordLength = 8;
    private const int MaxUserNameLength = 64;

    private readonly IAccountRepository _accounts;
    private readonly IAccountSessionRepository _sessions;
    private readonly PasswordHasher _passwordHasher;
    private readonly TimeProvider _time;
    private readonly AccountsOptions _options;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        IAccountRepository accounts,
        IAccountSessionRepository sessions,
        PasswordHasher passwordHasher,
        TimeProvider time,
        IOptions<AccountsOptions> options,
        ILogger<AccountService> logger)
    {
        _accounts = accounts;
        _sessions = sessions;
        _passwordHasher = passwordHasher;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AccountVM> RegisterAsync(RegisterAccountRequestVM request, CancellationToken cancellationToken = default)
    {
        if (!_options.AllowRegistration)
            throw ServiceException.Forbidden(RelayErrors.RegistrationDisabled, "Relay không cho tự đăng ký tài khoản.");
        var userName = ValidateUserName(request.UserName);
        if (request.Password is null || request.Password.Length < MinPasswordLength)
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, $"Mật khẩu tối thiểu {MinPasswordLength} ký tự.");
        return await CreateAsync(userName, _passwordHasher.Hash(request.Password), cancellationToken);
    }

    public async Task<AccountVM> SeedAsync(string userName, string passwordHash, CancellationToken cancellationToken = default)
    {
        var name = ValidateUserName(userName);
        if (string.IsNullOrWhiteSpace(passwordHash) || !_passwordHasher.IsWellFormed(passwordHash))
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, "Hash mật khẩu không đúng định dạng pbkdf2-sha256$<vòng lặp>$<salt>$<hash>.");
        return await CreateAsync(name, passwordHash, cancellationToken);
    }

    private static string ValidateUserName(string? userName)
    {
        var name = NormalizeUserName(userName ?? string.Empty);
        if (name.Length == 0 || name.Length > MaxUserNameLength || !name.All(IsUserNameChar))
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, "Tên đăng nhập 1-64 ký tự: chữ, số, '.', '_', '-'.");
        return name;
    }

    private async Task<AccountVM> CreateAsync(string userName, string passwordHash, CancellationToken cancellationToken)
    {
        var created = await _accounts.CreateAsync(new NewAccountRecordVM(userName, passwordHash), cancellationToken)
            ?? throw ServiceException.Conflict(RelayErrors.UserNameTaken, "Tên đăng nhập đã có người dùng.");
        _logger.LogInformation("Đã tạo tài khoản {AccountId} ({UserName}).", created.Id, created.UserName);
        return ToVM(created);
    }

    public async Task<LoginResultVM> LoginAsync(LoginRequestVM request, CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByUserNameAsync(NormalizeUserName(request.UserName ?? string.Empty), cancellationToken);
        // Verify even when the user does not exist (against a dummy hash of the same cost): the time must not tell
        // an unknown user name from a wrong password.
        var passwordOk = _passwordHasher.Verify(request.Password ?? string.Empty, account?.PasswordHash ?? _passwordHasher.DummyHash);
        if (account is null || request.Password is null || !passwordOk)
            throw ServiceException.Unauthorized(RelayErrors.InvalidCredentials, "Sai tên đăng nhập hoặc mật khẩu.");

        var token = SecretTokens.Create(RelayAuth.SessionTokenPrefix);
        var expiresAt = _time.GetUtcNow().UtcDateTime + _options.SessionLifetime;
        await _sessions.CreateAsync(new NewAccountSessionRecordVM(account.Id, SecretTokens.Hash(token), expiresAt), cancellationToken);
        return new LoginResultVM(token, expiresAt, ToVM(account));
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => await _sessions.RevokeAsync(sessionId, _time.GetUtcNow().UtcDateTime, cancellationToken);

    public async Task<AccountVM> GetAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken)
            ?? throw ServiceException.NotFound(RelayErrors.AccountNotFound, "Không tìm thấy tài khoản.");
        return ToVM(account);
    }

    public async Task<AccountSessionIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!token.StartsWith(RelayAuth.SessionTokenPrefix, StringComparison.Ordinal))
            return null;
        var session = await _sessions.GetByTokenHashAsync(SecretTokens.Hash(token), cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= _time.GetUtcNow().UtcDateTime)
            return null;
        return new AccountSessionIdentity(session.AccountId, session.Id);
    }

    private static string NormalizeUserName(string userName) => (userName ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsUserNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private static AccountVM ToVM(AccountRecordVM record) => new(record.Id, record.UserName, record.CreatedAt);
}
