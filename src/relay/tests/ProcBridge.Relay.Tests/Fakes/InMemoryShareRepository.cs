using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Tests.Fakes;

/// <summary>Mirrors <c>ShareRepository</c>: owner joined from the host device, cascade of grants on delete, revoked devices hidden from the accessible list.</summary>
public sealed class InMemoryShareRepository : IShareRepository
{
    private readonly InMemoryRelayStore _store;

    public InMemoryShareRepository(InMemoryRelayStore store) => _store = store;

    public Task<ShareRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult(Records().FirstOrDefault(x => x.Id == id));
    }

    public Task<IReadOnlyList<ShareRecordVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult<IReadOnlyList<ShareRecordVM>>(Records().Where(x => x.OwnerAccountId == ownerAccountId).ToList());
    }

    public Task<ShareRecordVM> CreateAsync(NewShareRecordVM share, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            var id = Guid.NewGuid();
            _store.Shares.Add((id, share.DeviceId, share.Name, DateTime.UtcNow));
            return Task.FromResult(Records().First(x => x.Id == id));
        }
    }

    public Task<bool> HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            _store.Grants.RemoveAll(x => x.ShareId == id);
            return Task.FromResult(_store.Shares.RemoveAll(x => x.Id == id) > 0);
        }
    }

    public Task<IReadOnlyList<ShareGrantRecordVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult<IReadOnlyList<ShareGrantRecordVM>>(_store.Grants.Where(x => x.ShareId == shareId).ToList());
    }

    public Task<ShareGrantRecordVM> EnsureGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            var existing = _store.Grants.FirstOrDefault(x => x.ShareId == shareId && x.GranteeDeviceId == granteeDeviceId);
            if (existing is not null)
                return Task.FromResult(existing);
            var grant = new ShareGrantRecordVM(shareId, granteeDeviceId, DateTime.UtcNow);
            _store.Grants.Add(grant);
            return Task.FromResult(grant);
        }
    }

    public Task<bool> DeleteGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult(_store.Grants.RemoveAll(x => x.ShareId == shareId && x.GranteeDeviceId == granteeDeviceId) > 0);
    }

    public Task<bool> GrantExistsAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult(_store.Grants.Any(x => x.ShareId == shareId && x.GranteeDeviceId == granteeDeviceId));
    }

    public Task<IReadOnlyList<AccessibleShareRecordVM>> GetAccessibleAsync(Guid granteeDeviceId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            var devices = _store.Devices.Select(x => x.Device).ToDictionary(x => x.Id);
            if (!devices.TryGetValue(granteeDeviceId, out var grantee) || grantee.RevokedAt is not null)
                return Task.FromResult<IReadOnlyList<AccessibleShareRecordVM>>([]);
            var result = _store.Grants
                .Where(g => g.GranteeDeviceId == granteeDeviceId)
                .Join(_store.Shares, g => g.ShareId, s => s.Id, (g, s) => s)
                .Where(s => devices[s.DeviceId].RevokedAt is null)
                .OrderBy(s => s.Name)
                .Select(s => new AccessibleShareRecordVM(s.Id, s.Name, s.DeviceId, devices[s.DeviceId].Name, devices[s.DeviceId].PublicKey))
                .ToList();
            return Task.FromResult<IReadOnlyList<AccessibleShareRecordVM>>(result);
        }
    }

    private IEnumerable<ShareRecordVM> Records()
    {
        var devices = _store.Devices.Select(x => x.Device).ToDictionary(x => x.Id);
        return _store.Shares.Select(s => new ShareRecordVM(s.Id, s.DeviceId, devices[s.DeviceId].OwnerAccountId, s.Name, s.CreatedAt));
    }
}
