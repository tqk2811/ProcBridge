using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Accounts.Interfaces;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController : ControllerBase, IRelayAccountsController
{
    private readonly IAccountService _accounts;

    public AccountsController(IAccountService accounts) => _accounts = accounts;

    [HttpPost("register")]
    [AllowAnonymous]
    public Task<AccountVM> RegisterAsync([FromBody] RegisterAccountRequestVM request, CancellationToken cancellationToken = default)
        => _accounts.RegisterAsync(request, cancellationToken);

    [HttpPost("login")]
    [AllowAnonymous]
    public Task<LoginResultVM> LoginAsync([FromBody] LoginRequestVM request, CancellationToken cancellationToken = default)
        => _accounts.LoginAsync(request, cancellationToken);

    [HttpPost("logout")]
    [Authorize(Policy = RelayAuth.AccountPolicy)]
    public Task LogoutAsync(CancellationToken cancellationToken = default)
        => _accounts.LogoutAsync(User.GetSessionId(), cancellationToken);

    [HttpGet("me")]
    [Authorize(Policy = RelayAuth.AccountPolicy)]
    public Task<AccountVM> GetMeAsync(CancellationToken cancellationToken = default)
        => _accounts.GetAsync(User.GetAccountId(), cancellationToken);
}
