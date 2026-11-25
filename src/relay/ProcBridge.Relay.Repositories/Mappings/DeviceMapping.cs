using System.Linq.Expressions;

using ProcBridge.Relay.Database.Entities;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Mappings;

internal static class DeviceMapping
{
    public static readonly Expression<Func<DeviceDbo, DeviceRecordVM>> ToRecordExpression =
        x => new DeviceRecordVM(x.Id, x.OwnerAccountId, x.Name, x.PublicKey, x.CreatedAt, x.RevokedAt);
    private static readonly Func<DeviceDbo, DeviceRecordVM> _toRecord = ToRecordExpression.Compile();

    public static DeviceRecordVM ToRecord(this DeviceDbo dbo) => _toRecord(dbo);

    public static DeviceDbo ToDbo(this NewDeviceRecordVM record)
        => new() { OwnerAccountId = record.OwnerAccountId, Name = record.Name, PublicKey = record.PublicKey, TokenHash = record.TokenHash };
}
