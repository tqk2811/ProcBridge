using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Integration.Api.Implements;

public sealed partial class RelayApiImplement
{
    private sealed class AccountsControllerImplement : IRelayAccountsController
    {
        private const string BaseUrl = "api/accounts";
        private readonly RelayApiImplement _api;

        public AccountsControllerImplement(RelayApiImplement api) => _api = api;

        public Task<AccountVM> RegisterAsync(RegisterAccountRequestVM request, CancellationToken cancellationToken = default)
            => _api.SendAsync<AccountVM>(HttpMethod.Post, $"{BaseUrl}/register", request, cancellationToken);

        public Task<LoginResultVM> LoginAsync(LoginRequestVM request, CancellationToken cancellationToken = default)
            => _api.SendAsync<LoginResultVM>(HttpMethod.Post, $"{BaseUrl}/login", request, cancellationToken);

        public Task LogoutAsync(CancellationToken cancellationToken = default)
            => _api.SendVoidAsync(HttpMethod.Post, $"{BaseUrl}/logout", null, cancellationToken);

        public Task<AccountVM> GetMeAsync(CancellationToken cancellationToken = default)
            => _api.SendAsync<AccountVM>(HttpMethod.Get, $"{BaseUrl}/me", null, cancellationToken);
    }
}
