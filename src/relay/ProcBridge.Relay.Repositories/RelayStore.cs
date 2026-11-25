using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Repositories;

/// <summary>
/// The relay's data: rows kept in this process (singleton). Nothing is persisted: a relay restart starts empty and
/// accounts come back only from <c>Relay:Accounts:Seed</c> (decision of the project owner, see docs/Plan-vi.md 8.2).
/// Every access goes through <see cref="Lock"/>.
/// </summary>
public sealed class RelayStore
{
    public object Lock { get; } = new();
    public List<(AccountRecordVM Account, string NormalizedName)> Accounts { get; } = new();
    public List<(AccountSessionRecordVM Session, string TokenHash)> Sessions { get; } = new();
    public List<(DeviceRecordVM Device, string TokenHash)> Devices { get; } = new();
    public List<(Guid Id, Guid DeviceId, string Name, DateTime CreatedAt)> Shares { get; } = new();
    public List<ShareGrantRecordVM> Grants { get; } = new();
}
