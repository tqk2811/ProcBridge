using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Testing.Fakes;

public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly InMemoryRelayStore _store;

    public InMemoryDeviceRepository(InMemoryRelayStore store) => _store = store;

    public Task<DeviceRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult(_store.Devices.Select(x => x.Device).FirstOrDefault(x => x.Id == id));
    }

    public Task<DeviceRecordVM?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult<DeviceRecordVM?>(_store.Devices.FirstOrDefault(x => x.TokenHash == tokenHash).Device);
    }

    public Task<IReadOnlyList<DeviceRecordVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
            return Task.FromResult<IReadOnlyList<DeviceRecordVM>>(_store.Devices.Select(x => x.Device).Where(x => x.OwnerAccountId == ownerAccountId).ToList());
    }

    public Task<DeviceRecordVM> CreateAsync(NewDeviceRecordVM device, CancellationToken cancellationToken = default)
    {
        var record = new DeviceRecordVM(Guid.NewGuid(), device.OwnerAccountId, device.Name, device.PublicKey, DateTime.UtcNow, null);
        lock (_store.Lock)
            _store.Devices.Add((record, device.TokenHash));
        return Task.FromResult(record);
    }

    public Task<bool> RevokeAsync(Guid id, DateTime revokedAt, CancellationToken cancellationToken = default)
    {
        lock (_store.Lock)
        {
            var index = _store.Devices.FindIndex(x => x.Device.Id == id && x.Device.RevokedAt == null);
            if (index < 0)
                return Task.FromResult(false);
            var row = _store.Devices[index];
            _store.Devices[index] = (row.Device with { RevokedAt = revokedAt }, row.TokenHash);
            return Task.FromResult(true);
        }
    }
}
