using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Abstractions.Interfaces;

public interface IShareRepository
{
    Task<ShareRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Shares served by any device of the account.</summary>
    Task<IReadOnlyList<ShareRecordVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default);
    Task<ShareRecordVM> CreateAsync(NewShareRecordVM share, CancellationToken cancellationToken = default);
    /// <summary>Hard delete; grants of the share go with it (cascade).</summary>
    /// <returns><c>false</c> when there is no such share.</returns>
    Task<bool> HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShareGrantRecordVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default);
    /// <summary>Idempotent: granting twice keeps the first row and returns it.</summary>
    Task<ShareGrantRecordVM> EnsureGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default);
    /// <returns><c>false</c> when the grant did not exist.</returns>
    Task<bool> DeleteGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default);
    Task<bool> GrantExistsAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default);
    /// <summary>Shares the (non-revoked) device was granted, whose host device is not revoked either.</summary>
    Task<IReadOnlyList<AccessibleShareRecordVM>> GetAccessibleAsync(Guid granteeDeviceId, CancellationToken cancellationToken = default);
}
