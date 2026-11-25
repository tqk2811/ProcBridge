using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Abstractions.Interfaces;

public interface IAccountRepository
{
    Task<AccountRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <param name="userName">Already normalized (lower-cased) by the caller.</param>
    Task<AccountRecordVM?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default);
    /// <summary>Creates the account; returns <c>null</c> when the user name is already taken (unique index).</summary>
    Task<AccountRecordVM?> CreateAsync(NewAccountRecordVM account, CancellationToken cancellationToken = default);
}
