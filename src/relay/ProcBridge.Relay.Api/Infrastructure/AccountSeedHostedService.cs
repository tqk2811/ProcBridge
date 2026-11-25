using Microsoft.Extensions.Options;

using ProcBridge.Relay.Services.Accounts.Interfaces;
using ProcBridge.Relay.Services.Accounts.Models;
using ProcBridge.Relay.Services.Common.Common.Errors;

namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Creates the accounts of <c>Relay:Accounts:Seed</c> when the relay starts. The data lives in memory, so this runs on
/// every start. A bad entry stops the start (a relay that silently lacks its operator account is worse than none).
/// </summary>
public sealed class AccountSeedHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AccountsOptions _options;
    private readonly ILogger<AccountSeedHostedService> _logger;

    public AccountSeedHostedService(IServiceScopeFactory scopes, IOptions<AccountsOptions> options, ILogger<AccountSeedHostedService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        foreach (var seed in _options.Seed)
        {
            try
            {
                await accounts.SeedAsync(seed.UserName, seed.PasswordHash, cancellationToken);
            }
            catch (ServiceException ex)
            {
                throw new InvalidOperationException($"Relay:Accounts:Seed '{seed.UserName}': {ex.Code}: {ex.Message}", ex);
            }
        }
        if (_options.Seed.Count == 0 && !_options.AllowRegistration)
            _logger.LogWarning("Relay không có tài khoản nào: đặt Relay:Accounts:Seed hoặc bật Relay:Accounts:AllowRegistration.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
