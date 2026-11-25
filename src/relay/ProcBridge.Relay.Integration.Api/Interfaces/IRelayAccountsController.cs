using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Group <c>api/accounts</c>.</summary>
public interface IRelayAccountsController
{
    /// <summary><c>POST api/accounts/register</c> (anonymous).</summary>
    Task<AccountVM> RegisterAsync(RegisterAccountRequestVM request, CancellationToken cancellationToken = default);

    /// <summary><c>POST api/accounts/login</c> (anonymous).</summary>
    Task<LoginResultVM> LoginAsync(LoginRequestVM request, CancellationToken cancellationToken = default);

    /// <summary><c>POST api/accounts/logout</c> (account token).</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary><c>GET api/accounts/me</c> (account token).</summary>
    Task<AccountVM> GetMeAsync(CancellationToken cancellationToken = default);
}
