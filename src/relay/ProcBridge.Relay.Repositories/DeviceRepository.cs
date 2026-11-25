using Microsoft.EntityFrameworkCore;

using ProcBridge.Relay.Database.PostgreSql.EF;
using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Repositories.Mappings;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories;

public sealed class DeviceRepository : IDeviceRepository
{
    private readonly RelayDbContext _db;

    public DeviceRepository(RelayDbContext db) => _db = db;

    public async Task<DeviceRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _db.Devices.AsNoTracking().Where(x => x.Id == id).Select(DeviceMapping.ToRecordExpression).FirstOrDefaultAsync(cancellationToken);

    public async Task<DeviceRecordVM?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => await _db.Devices.AsNoTracking().Where(x => x.TokenHash == tokenHash).Select(DeviceMapping.ToRecordExpression).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DeviceRecordVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default)
        => await _db.Devices.AsNoTracking()
            .Where(x => x.OwnerAccountId == ownerAccountId)
            .OrderBy(x => x.CreatedAt)
            .Select(DeviceMapping.ToRecordExpression)
            .ToListAsync(cancellationToken);

    public async Task<DeviceRecordVM> CreateAsync(NewDeviceRecordVM device, CancellationToken cancellationToken = default)
    {
        var row = device.ToDbo();
        _db.Devices.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return row.ToRecord();
    }

    public async Task<bool> RevokeAsync(Guid id, DateTime revokedAt, CancellationToken cancellationToken = default)
    {
        var row = await _db.Devices.FirstOrDefaultAsync(x => x.Id == id && x.RevokedAt == null, cancellationToken);
        if (row is null)
            return false;
        row.RevokedAt = revokedAt;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
