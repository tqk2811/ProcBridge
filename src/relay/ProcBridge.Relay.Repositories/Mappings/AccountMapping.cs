using System.Linq.Expressions;

using ProcBridge.Relay.Database.Entities;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Mappings;

internal static class AccountMapping
{
    public static readonly Expression<Func<AccountDbo, AccountRecordVM>> ToRecordExpression =
        x => new AccountRecordVM(x.Id, x.UserName, x.PasswordHash, x.CreatedAt);
    private static readonly Func<AccountDbo, AccountRecordVM> _toRecord = ToRecordExpression.Compile();

    public static AccountRecordVM ToRecord(this AccountDbo dbo) => _toRecord(dbo);

    public static AccountDbo ToDbo(this NewAccountRecordVM record)
        => new() { UserName = record.UserName, PasswordHash = record.PasswordHash };
}
