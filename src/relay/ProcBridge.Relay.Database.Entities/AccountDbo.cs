using System.ComponentModel.DataAnnotations;

namespace ProcBridge.Relay.Database.Entities;

/// <summary>A person who signs in to the relay and owns devices.</summary>
public sealed class AccountDbo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Stored lower-cased by the service, so the unique index is case-insensitive in effect.</summary>
    [MaxLength(64)] public required string UserName { get; set; }
    /// <summary>PBKDF2 hash in the self-describing format of <c>PasswordHasher</c> (Services.Common).</summary>
    [MaxLength(256)] public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DeviceDbo> Devices { get; set; } = new List<DeviceDbo>();
    public ICollection<AccountSessionDbo> Sessions { get; set; } = new List<AccountSessionDbo>();
}
