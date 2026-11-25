using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Abstractions.Interfaces;

public interface IAccountSessionRepository
{
    Task<AccountSessionRecordVM> CreateAsync(NewAccountSessionRecordVM session, CancellationToken cancellationToken = default);
    Task<AccountSessionRecordVM?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);
    /// <returns><c>false</c> when there is no such session or it was already revoked.</returns>
    Task<bool> RevokeAsync(Guid id, DateTime revokedAt, CancellationToken cancellationToken = default);
}
