using Microsoft.EntityFrameworkCore;

using ProcBridge.Relay.Database.PostgreSql.EF;
using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Repositories.Mappings;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories;

public sealed class AccountSessionRepository : IAccountSessionRepository
{
    private readonly RelayDbContext _db;

    public AccountSessionRepository(RelayDbContext db) => _db = db;

    public async Task<AccountSessionRecordVM> CreateAsync(NewAccountSessionRecordVM session, CancellationToken cancellationToken = default)
    {
        var row = session.ToDbo();
        _db.AccountSessions.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return row.ToRecord();
    }

    public async Task<AccountSessionRecordVM?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => await _db.AccountSessions.AsNoTracking().Where(x => x.TokenHash == tokenHash).Select(AccountSessionMapping.ToRecordExpression).FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> RevokeAsync(Guid id, DateTime revokedAt, CancellationToken cancellationToken = default)
    {
        var row = await _db.AccountSessions.FirstOrDefaultAsync(x => x.Id == id && x.RevokedAt == null, cancellationToken);
        if (row is null)
            return false;
        row.RevokedAt = revokedAt;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
