using System.Linq.Expressions;

using ProcBridge.Relay.Database.Entities;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Mappings;

internal static class AccountSessionMapping
{
    public static readonly Expression<Func<AccountSessionDbo, AccountSessionRecordVM>> ToRecordExpression =
        x => new AccountSessionRecordVM(x.Id, x.AccountId, x.ExpiresAt, x.RevokedAt);
    private static readonly Func<AccountSessionDbo, AccountSessionRecordVM> _toRecord = ToRecordExpression.Compile();

    public static AccountSessionRecordVM ToRecord(this AccountSessionDbo dbo) => _toRecord(dbo);

    public static AccountSessionDbo ToDbo(this NewAccountSessionRecordVM record)
        => new() { AccountId = record.AccountId, TokenHash = record.TokenHash, ExpiresAt = record.ExpiresAt };
}
