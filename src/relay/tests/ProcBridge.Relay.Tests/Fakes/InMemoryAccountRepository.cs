using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Tests.Fakes;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly InMemoryRelayStore _store;

    public InMemoryAccountRepository(InMemoryRelayStore store) => _store = store;

    public Task<AccountRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult(_store.Accounts.Select(x => x.Account).FirstOrDefault(x => x.Id == id));
    }

    public Task<AccountRecordVM?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult(_store.Accounts.Select(x => x.Account).FirstOrDefault(x => x.UserName == userName));
    }

    public Task<AccountRecordVM?> CreateAsync(NewAccountRecordVM account, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            if (_store.Accounts.Any(x => x.Account.UserName == account.UserName))
                return Task.FromResult<AccountRecordVM?>(null);
            var record = new AccountRecordVM(Guid.NewGuid(), account.UserName, account.PasswordHash, DateTime.UtcNow);
            _store.Accounts.Add((record, account.UserName));
            return Task.FromResult<AccountRecordVM?>(record);
        }
    }
}
