using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Tests.Fakes;

/// <summary>Rows of the in-memory relay database (one per factory). Every access goes through <see cref="Lock"/>.</summary>
public sealed class InMemoryRelayStore
{
    public object Lock { get; } = new();
    public List<(AccountRecordVM Account, string NormalizedName)> Accounts { get; } = new();
    public List<(AccountSessionRecordVM Session, string TokenHash)> Sessions { get; } = new();
    public List<(DeviceRecordVM Device, string TokenHash)> Devices { get; } = new();
    public List<(Guid Id, Guid DeviceId, string Name, DateTime CreatedAt)> Shares { get; } = new();
    public List<ShareGrantRecordVM> Grants { get; } = new();
}
