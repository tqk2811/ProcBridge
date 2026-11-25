using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Abstractions.Interfaces;

public interface IDeviceRepository
{
    Task<DeviceRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DeviceRecordVM?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceRecordVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default);
    Task<DeviceRecordVM> CreateAsync(NewDeviceRecordVM device, CancellationToken cancellationToken = default);
    /// <summary>Soft revoke: the token stops working, rows that point at the device (grants, shares) stay.</summary>
    /// <returns><c>false</c> when there is no such device or it was already revoked.</returns>
    Task<bool> RevokeAsync(Guid id, DateTime revokedAt, CancellationToken cancellationToken = default);
}
