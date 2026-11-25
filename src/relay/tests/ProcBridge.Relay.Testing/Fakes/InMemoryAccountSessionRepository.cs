using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Testing.Fakes;

public sealed class InMemoryAccountSessionRepository : IAccountSessionRepository
{
    private readonly InMemoryRelayStore _store;

    public InMemoryAccountSessionRepository(InMemoryRelayStore store) => _store = store;

    public Task<AccountSessionRecordVM> CreateAsync(NewAccountSessionRecordVM session, CancellationToken cancellationToken = default)
    {
        var record = new AccountSessionRecordVM(Guid.NewGuid(), session.AccountId, session.ExpiresAt, null);
        lock (_store.Lock)
            _store.Sessions.Add((record, session.TokenHash));
        return Task.FromResult(record);
    }

    public Task<AccountSessionRecordVM?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult<AccountSessionRecordVM?>(_store.Sessions.FirstOrDefault(x => x.TokenHash == tokenHash).Session);
    }

    public Task<bool> RevokeAsync(Guid id, DateTime revokedAt, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            var index = _store.Sessions.FindIndex(x => x.Session.Id == id && x.Session.RevokedAt == null);
            if (index < 0)
                return Task.FromResult(false);
            var row = _store.Sessions[index];
            _store.Sessions[index] = (row.Session with { RevokedAt = revokedAt }, row.TokenHash);
            return Task.FromResult(true);
        }
    }
}
