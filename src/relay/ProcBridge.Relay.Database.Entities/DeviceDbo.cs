using System.ComponentModel.DataAnnotations;

namespace ProcBridge.Relay.Database.Entities;

/// <summary>A registered machine (Broker on machine A or Agent on machine B) with its own long-lived token.</summary>
public sealed class DeviceDbo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OwnerAccountId { get; set; }
    [MaxLength(128)] public required string Name { get; set; }
    /// <summary>Public identity key (base64) relayed to peers; the relay never uses it to decrypt anything.</summary>
    [MaxLength(512)] public string? PublicKey { get; set; }
    /// <summary>SHA-256 hex of the device token. The token itself is never stored.</summary>
    [MaxLength(64)] public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    public AccountDbo? OwnerAccount { get; set; }
    public ICollection<ShareDbo> Shares { get; set; } = new List<ShareDbo>();
    public ICollection<ShareGrantDbo> Grants { get; set; } = new List<ShareGrantDbo>();
}
