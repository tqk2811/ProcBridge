using ProcBridge.Relay.Services.Accounts.Models;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Services.Accounts.Interfaces;

public interface IAccountService
{
    Task<AccountVM> RegisterAsync(RegisterAccountRequestVM request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Creates an account from a ready-made password hash whatever <c>AllowRegistration</c> says (start-up seeding from
    /// <c>Relay:Accounts:Seed</c>); throws a <c>ServiceException</c> when the name or hash is invalid or the name is taken.
    /// </summary>
    Task<AccountVM> SeedAsync(string userName, string passwordHash, CancellationToken cancellationToken = default);
    Task<LoginResultVM> LoginAsync(LoginRequestVM request, CancellationToken cancellationToken = default);
    Task LogoutAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AccountVM> GetAsync(Guid accountId, CancellationToken cancellationToken = default);
    /// <summary><c>null</c> when the token is unknown, expired or revoked.</summary>
    Task<AccountSessionIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken = default);
}
