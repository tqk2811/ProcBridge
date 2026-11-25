using System.ComponentModel.DataAnnotations;

namespace ProcBridge.Relay.Database.Entities;

/// <summary>Something a host device (machine B) offers to peers through the relay.</summary>
public sealed class ShareDbo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid DeviceId { get; set; }
    [MaxLength(128)] public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DeviceDbo? Device { get; set; }
    public ICollection<ShareGrantDbo> Grants { get; set; } = new List<ShareGrantDbo>();
}
