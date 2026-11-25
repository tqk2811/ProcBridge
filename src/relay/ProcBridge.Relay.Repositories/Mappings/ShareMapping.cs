using System.Linq.Expressions;

using ProcBridge.Relay.Database.Entities;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories.Mappings;

internal static class ShareMapping
{
    public static readonly Expression<Func<ShareDbo, ShareRecordVM>> ToRecordExpression =
        x => new ShareRecordVM(x.Id, x.DeviceId, x.Device!.OwnerAccountId, x.Name, x.CreatedAt);

    public static readonly Expression<Func<ShareGrantDbo, ShareGrantRecordVM>> ToGrantRecordExpression =
        x => new ShareGrantRecordVM(x.ShareId, x.GranteeDeviceId, x.CreatedAt);
    private static readonly Func<ShareGrantDbo, ShareGrantRecordVM> _toGrantRecord = ToGrantRecordExpression.Compile();

    public static readonly Expression<Func<ShareGrantDbo, AccessibleShareRecordVM>> ToAccessibleRecordExpression =
        x => new AccessibleShareRecordVM(x.ShareId, x.Share!.Name, x.Share.DeviceId, x.Share.Device!.Name, x.Share.Device.PublicKey);

    public static ShareGrantRecordVM ToRecord(this ShareGrantDbo dbo) => _toGrantRecord(dbo);

    public static ShareDbo ToDbo(this NewShareRecordVM record) => new() { DeviceId = record.DeviceId, Name = record.Name };
}
