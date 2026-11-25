using Microsoft.EntityFrameworkCore;

using Npgsql;

using ProcBridge.Relay.Database.Entities;
using ProcBridge.Relay.Database.PostgreSql.EF;
using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Repositories.Mappings;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories;

public sealed class ShareRepository : IShareRepository
{
    private readonly RelayDbContext _db;

    public ShareRepository(RelayDbContext db) => _db = db;

    public async Task<ShareRecordVM?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await _db.Shares.AsNoTracking().Where(x => x.Id == id).Select(ShareMapping.ToRecordExpression).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ShareRecordVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default)
        => await _db.Shares.AsNoTracking()
            .Where(x => x.Device!.OwnerAccountId == ownerAccountId)
            .OrderBy(x => x.CreatedAt)
            .Select(ShareMapping.ToRecordExpression)
            .ToListAsync(cancellationToken);

    public async Task<ShareRecordVM> CreateAsync(NewShareRecordVM share, CancellationToken cancellationToken = default)
    {
        var row = share.ToDbo();
        _db.Shares.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        // Read back for the joined owner column; the write is committed, so do not let a cancel fake a failure.
        return await _db.Shares.AsNoTracking().Where(x => x.Id == row.Id).Select(ShareMapping.ToRecordExpression).FirstAsync(CancellationToken.None);
    }

    public async Task<bool> HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => await _db.Shares.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<ShareGrantRecordVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default)
        => await _db.ShareGrants.AsNoTracking()
            .Where(x => x.ShareId == shareId)
            .OrderBy(x => x.CreatedAt)
            .Select(ShareMapping.ToGrantRecordExpression)
            .ToListAsync(cancellationToken);

    public async Task<ShareGrantRecordVM> EnsureGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default)
    {
        var existing = await FindGrantAsync(shareId, granteeDeviceId, cancellationToken);
        if (existing is not null)
            return existing;

        var row = new ShareGrantDbo { ShareId = shareId, GranteeDeviceId = granteeDeviceId };
        _db.ShareGrants.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return row.ToRecord();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent grant of the same pair won the unique index: return the winner.
            _db.Entry(row).State = EntityState.Detached;
            return await FindGrantAsync(shareId, granteeDeviceId, CancellationToken.None)
                ?? throw new InvalidOperationException("Grant vanished right after a unique violation.");
        }
    }

    public async Task<bool> DeleteGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default)
        => await _db.ShareGrants.Where(x => x.ShareId == shareId && x.GranteeDeviceId == granteeDeviceId).ExecuteDeleteAsync(cancellationToken) > 0;

    public Task<bool> GrantExistsAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken = default)
        => _db.ShareGrants.AnyAsync(x => x.ShareId == shareId && x.GranteeDeviceId == granteeDeviceId, cancellationToken);

    public async Task<IReadOnlyList<AccessibleShareRecordVM>> GetAccessibleAsync(Guid granteeDeviceId, CancellationToken cancellationToken = default)
        => await _db.ShareGrants.AsNoTracking()
            .Where(x => x.GranteeDeviceId == granteeDeviceId
                && x.GranteeDevice!.RevokedAt == null
                && x.Share!.Device!.RevokedAt == null)
            .OrderBy(x => x.Share!.Name)
            .Select(ShareMapping.ToAccessibleRecordExpression)
            .ToListAsync(cancellationToken);

    private async Task<ShareGrantRecordVM?> FindGrantAsync(Guid shareId, Guid granteeDeviceId, CancellationToken cancellationToken)
        => await _db.ShareGrants.AsNoTracking()
            .Where(x => x.ShareId == shareId && x.GranteeDeviceId == granteeDeviceId)
            .Select(ShareMapping.ToGrantRecordExpression)
            .FirstOrDefaultAsync(cancellationToken);
}
