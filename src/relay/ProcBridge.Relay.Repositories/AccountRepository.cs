using Microsoft.EntityFrameworkCore;

using Npgsql;

using ProcBridge.Relay.Database.PostgreSql.EF;
using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Repositories.Mappings;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories;

public sealed class AccountRepository : IAccountRepository
{
    private readonly RelayDbContext _db;

    public AccountRepository(RelayDbContext db) => _db = db;

    public async Task<AccountRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _db.Accounts.AsNoTracking().Where(x => x.Id == id).Select(AccountMapping.ToRecordExpression).FirstOrDefaultAsync(cancellationToken);

    public async Task<AccountRecordVM?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default)
        => await _db.Accounts.AsNoTracking().Where(x => x.UserName == userName).Select(AccountMapping.ToRecordExpression).FirstOrDefaultAsync(cancellationToken);

    public async Task<AccountRecordVM?> CreateAsync(NewAccountRecordVM account, CancellationToken cancellationToken = default)
    {
        var row = account.ToDbo();
        _db.Accounts.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.Entry(row).State = EntityState.Detached;
            return null;
        }
        return row.ToRecord();
    }
}
