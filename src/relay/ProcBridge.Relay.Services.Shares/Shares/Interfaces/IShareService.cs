using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Services.Shares.Interfaces;

/// <summary>
/// Shares and grants. Does NOT check permissions: controllers call <c>ShareAccessService</c> first
/// (owner of the device / owner of the share).
/// </summary>
public interface IShareService
{
    Task<ShareVM> CreateAsync(CreateShareRequestVM request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ShareVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid shareId, CancellationToken cancellationToken = default);
    Task<ShareGrantVM> GrantAsync(Guid shareId, GrantShareRequestVM request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ShareGrantVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default);
    Task RevokeGrantAsync(Guid shareId, Guid deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccessibleShareVM>> GetAccessibleAsync(Guid clientDeviceId, CancellationToken cancellationToken = default);
}
