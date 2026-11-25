namespace ProcBridge.Relay.Database.Entities;

/// <summary>Permission: client device <see cref="GranteeDeviceId"/> may open tunnel sessions to share <see cref="ShareId"/>.</summary>
public sealed class ShareGrantDbo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid ShareId { get; set; }
    public required Guid GranteeDeviceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ShareDbo? Share { get; set; }
    public DeviceDbo? GranteeDevice { get; set; }
}
