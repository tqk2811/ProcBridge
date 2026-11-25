using System.Security.Cryptography;
using System.Text.Json;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Identity.Interfaces;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// Machines this machine paired with, by fingerprint (both sides keep one: A lists its Agents, B lists the A machines
/// allowed in). It is the static key verifier of every Noise IK handshake, so revoking a machine refuses its next
/// connection. Persisted as JSON wrapped by an <see cref="IKeyProtector"/> (DPAPI in production): the keys are public,
/// the protector keeps other users from slipping a machine into the list. The parameterless constructor keeps the list
/// in memory (tests). Thread-safe. The protector is borrowed.
/// </summary>
public sealed class PairedMachineStore
{
    private const byte FormatVersion = 1;
    private static readonly byte[] _magic = "PBPM"u8.ToArray();

    private readonly string? _path;
    private readonly IKeyProtector? _protector;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PairedMachine> _machines = new(StringComparer.Ordinal);

    /// <summary>In-memory store.</summary>
    public PairedMachineStore()
    {
    }

    /// <summary>Store kept in <paramref name="path"/>; loads it when it exists.</summary>
    /// <exception cref="InvalidDataException">The file is not a paired machine list.</exception>
    /// <exception cref="CryptographicException">The file cannot be unwrapped (other user, corrupted).</exception>
    public PairedMachineStore(string path, IKeyProtector protector)
    {
        _path = path;
        _protector = protector;
        if (File.Exists(path))
            Load();
    }

    /// <summary>Raised after <see cref="Revoke"/> removed a machine, outside the store lock.</summary>
    public event Action<PairedMachine>? Revoked;

    public IReadOnlyList<PairedMachine> List()
    {
        lock (_lock)
            return _machines.Values.OrderBy(x => x.PairedAtUtc).ToArray();
    }

    public PairedMachine? Find(string fingerprint)
    {
        lock (_lock)
            return _machines.GetValueOrDefault(fingerprint);
    }

    /// <summary>The machine paired at <paramref name="address"/> (<c>host:port</c>, case-insensitive), the latest if several.</summary>
    public PairedMachine? FindByAddress(string address)
    {
        lock (_lock)
        {
            return _machines.Values
                .Where(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.PairedAtUtc)
                .FirstOrDefault();
        }
    }

    /// <summary>True when <paramref name="publicKey"/> is paired (handshake verifier).</summary>
    public bool IsTrusted(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != IdentityKey.PublicKeySize)
            return false;
        string fingerprint = KeyFingerprint.Compute(publicKey);
        lock (_lock)
            return _machines.TryGetValue(fingerprint, out PairedMachine? machine) && CryptographicOperations.FixedTimeEquals(machine.PublicKey, publicKey);
    }

    public bool IsTrusted(string fingerprint)
    {
        lock (_lock)
            return _machines.ContainsKey(fingerprint);
    }

    /// <summary>Adds or replaces the entry of <paramref name="publicKey"/> and saves the list; returns the entry.</summary>
    public PairedMachine Add(ReadOnlySpan<byte> publicKey, string name, string address = "")
    {
        if (publicKey.Length != IdentityKey.PublicKeySize)
            throw new ArgumentException($"Public key must be {IdentityKey.PublicKeySize} bytes.", nameof(publicKey));
        var machine = new PairedMachine
        {
            Fingerprint = KeyFingerprint.Compute(publicKey),
            PublicKey = publicKey.ToArray(),
            Name = name,
            Address = address,
            PairedAtUtc = DateTime.UtcNow,
        };
        lock (_lock)
        {
            _machines[machine.Fingerprint] = machine;
            SaveLocked();
        }
        return machine;
    }

    /// <summary>Removes the machine; its next handshake or channel is refused. False when it was not paired.</summary>
    public bool Revoke(string fingerprint)
    {
        PairedMachine? removed;
        lock (_lock)
        {
            if (!_machines.Remove(fingerprint, out removed))
                return false;
            SaveLocked();
        }
        Revoked?.Invoke(removed);
        return true;
    }

    private void Load()
    {
        byte[] data = File.ReadAllBytes(_path!);
        if (data.Length <= _magic.Length + 1 || !data.AsSpan(0, _magic.Length).SequenceEqual(_magic))
            throw new InvalidDataException("Not a ProcBridge paired machine file.");
        if (data[_magic.Length] != FormatVersion)
            throw new InvalidDataException($"Unsupported paired machine file version {data[_magic.Length]}.");
        byte[] json = _protector!.Unprotect(data[(_magic.Length + 1)..]);
        PairedMachine[] machines = JsonSerializer.Deserialize<PairedMachine[]>(json) ?? [];
        foreach (PairedMachine machine in machines)
        {
            if (machine.PublicKey is not { Length: IdentityKey.PublicKeySize } || KeyFingerprint.Compute(machine.PublicKey) != machine.Fingerprint)
                throw new InvalidDataException("Paired machine file holds an invalid entry.");
            _machines[machine.Fingerprint] = machine;
        }
    }

    /// <summary>Atomic write (temp file then replace) so a crash never leaves a half-written list.</summary>
    private void SaveLocked()
    {
        if (_path is null)
            return;
        byte[] blob = _protector!.Protect(JsonSerializer.SerializeToUtf8Bytes(_machines.Values.ToArray()));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(_magic);
                stream.WriteByte(FormatVersion);
                stream.Write(blob);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }
}
