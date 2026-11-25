using System.ComponentModel.DataAnnotations;

namespace ProcBridge.Relay.Database.Entities;

/// <summary>A sign-in session of an account (bearer token for the management REST API).</summary>
public sealed class AccountSessionDbo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid AccountId { get; set; }
    /// <summary>SHA-256 hex of the token. The token itself is never stored.</summary>
    [MaxLength(64)] public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public AccountDbo? Account { get; set; }
}
