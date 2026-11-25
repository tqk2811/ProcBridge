using ProcBridge.Relay.Services.Accounts.Models;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Services.Accounts.Interfaces;

public interface IAccountService
{
    Task<AccountVM> RegisterAsync(RegisterAccountRequestVM request, CancellationToken cancellationToken = default);
    /// <summary>Creates an account whatever <c>AllowRegistration</c> says (operator command on the relay host).</summary>
    Task<AccountVM> CreateByOperatorAsync(RegisterAccountRequestVM request, CancellationToken cancellationToken = default);
    Task<LoginResultVM> LoginAsync(LoginRequestVM request, CancellationToken cancellationToken = default);
    Task LogoutAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AccountVM> GetAsync(Guid accountId, CancellationToken cancellationToken = default);
    /// <summary><c>null</c> when the token is unknown, expired or revoked.</summary>
    Task<AccountSessionIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken = default);
}
