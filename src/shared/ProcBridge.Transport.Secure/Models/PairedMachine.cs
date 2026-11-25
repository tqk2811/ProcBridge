namespace ProcBridge.Transport.Secure.Models;

/// <summary>A machine whose identity key the user confirmed by pairing.</summary>
public sealed record PairedMachine
{
    /// <summary>Fingerprint of <see cref="PublicKey"/> (see <c>KeyFingerprint</c>), the key of the store.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Raw 32-byte X25519 identity public key.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>Name shown to the user.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Address the machine was paired at (<c>host:port</c>) on machine A; empty on machine B.</summary>
    public string Address { get; init; } = string.Empty;

    public DateTime PairedAtUtc { get; init; }
}
