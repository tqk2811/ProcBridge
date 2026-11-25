using System.Security.Cryptography;
using System.Text.Json;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Identity.Interfaces;
using ProcBridge.Crypto.Storage;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// Machines this machine paired with, by fingerprint (both sides keep one: A lists its Agents, B lists the A machines
/// allowed in). It is the static key verifier of every Noise IK handshake, so revoking a machine refuses its next
/// connection. Persisted as JSON wrapped by an <see cref="IKeyProtector"/> (DPAPI in production): the keys are public,
/// the protector keeps other users from slipping a machine into the list. The parameterless constructor keeps the list
/// in memory (tests). Thread-safe. The protector is borrowed.
/// <para>
/// The file is the truth, not the memory: other instances or processes (a settings tool revoking a machine while the
/// Agent runs) may change it. Every read first compares the file stamp and reloads when it changed, raising
/// <see cref="Revoked"/> for machines that disappeared; every write takes an inter-process lock, re-reads the file and
/// applies only its own change, so a stale list in memory never brings back a revoked machine. A running service calls
/// <see cref="Refresh"/> on a timer to notice a revocation while it sits idle.
/// </para>
/// </summary>
public sealed class PairedMachineStore
{
    private const byte FormatVersion = 1;
    private static readonly byte[] _magic = "PBPM"u8.ToArray();

    private readonly ProtectedStoreFile? _file;
    private readonly Lock _lock = new();
    private Dictionary<string, PairedMachine> _machines = new(StringComparer.Ordinal);

    /// <summary>In-memory store.</summary>
    public PairedMachineStore()
    {
    }

    /// <summary>Store kept in <paramref name="path"/>; loads it when it exists.</summary>
    /// <exception cref="InvalidDataException">The file is not a paired machine list.</exception>
    /// <exception cref="CryptographicException">The file cannot be unwrapped (other user, corrupted).</exception>
    public PairedMachineStore(string path, IKeyProtector protector)
    {
        _file = new ProtectedStoreFile(path, _magic, FormatVersion, protector);
        _machines = ReadFile();
    }

    /// <summary>
    /// Raised after a machine was removed, by <see cref="Revoke"/> here or by another instance or process that changed
    /// the file; outside the store lock. A handler that throws is ignored for machines found by a reload.
    /// </summary>
    public event Action<PairedMachine>? Revoked;

    public IReadOnlyList<PairedMachine> List()
    {
        IReadOnlyList<PairedMachine> result;
        IReadOnlyList<PairedMachine> removed;
        lock (_lock)
        {
            removed = RefreshLocked();
            result = _machines.Values.OrderBy(x => x.PairedAtUtc).ToArray();
        }
        Raise(removed);
        return result;
    }

    public PairedMachine? Find(string fingerprint)
    {
        PairedMachine? result;
        IReadOnlyList<PairedMachine> removed;
        lock (_lock)
        {
            removed = RefreshLocked();
            result = _machines.GetValueOrDefault(fingerprint);
        }
        Raise(removed);
        return result;
    }

    /// <summary>The machine paired at <paramref name="address"/> (<c>host:port</c>, case-insensitive), the latest if several.</summary>
    public PairedMachine? FindByAddress(string address)
    {
        PairedMachine? result;
        IReadOnlyList<PairedMachine> removed;
        lock (_lock)
        {
            removed = RefreshLocked();
            result = _machines.Values
                .Where(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.PairedAtUtc)
                .FirstOrDefault();
        }
        Raise(removed);
        return result;
    }

    /// <summary>True when <paramref name="publicKey"/> is paired (handshake verifier).</summary>
    public bool IsTrusted(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != IdentityKey.PublicKeySize)
            return false;
        string fingerprint = KeyFingerprint.Compute(publicKey);
        bool result;
        IReadOnlyList<PairedMachine> removed;
        lock (_lock)
        {
            removed = RefreshLocked();
            result = _machines.TryGetValue(fingerprint, out PairedMachine? machine) && CryptographicOperations.FixedTimeEquals(machine.PublicKey, publicKey);
        }
        Raise(removed);
        return result;
    }

    public bool IsTrusted(string fingerprint)
    {
        bool result;
        IReadOnlyList<PairedMachine> removed;
        lock (_lock)
        {
            removed = RefreshLocked();
            result = _machines.ContainsKey(fingerprint);
        }
        Raise(removed);
        return result;
    }

    /// <summary>Reloads the file when another instance or process changed it, raising <see cref="Revoked"/> for machines it dropped.</summary>
    public void Refresh()
    {
        IReadOnlyList<PairedMachine> removed;
        lock (_lock)
            removed = RefreshLocked();
        Raise(removed);
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
        Mutate(machines =>
        {
            machines[machine.Fingerprint] = machine;
            return true;
        }, out IReadOnlyList<PairedMachine> removed);
        Raise(removed);
        return machine;
    }

    /// <summary>Removes the machine; its next handshake or channel is refused. False when it was not paired.</summary>
    public bool Revoke(string fingerprint)
    {
        bool revoked = Mutate(machines => machines.Remove(fingerprint), out IReadOnlyList<PairedMachine> removed);
        Raise(removed, rethrow: true);
        return revoked;
    }

    /// <summary>Applies <paramref name="change"/> to the current content of the file (not to the copy in memory) and saves it.</summary>
    private bool Mutate(Func<Dictionary<string, PairedMachine>, bool> change, out IReadOnlyList<PairedMachine> removed)
    {
        lock (_lock)
        {
            if (_file is null)
            {
                PairedMachine[] before = _machines.Values.ToArray();
                bool applied = change(_machines);
                removed = before.Where(x => !_machines.ContainsKey(x.Fingerprint)).ToArray();
                return applied;
            }

            using (_file.Lock())
            {
                Dictionary<string, PairedMachine> current = ReadFile();
                bool applied = change(current);
                if (applied)
                    WriteFile(current);
                removed = _machines.Values.Where(x => !current.ContainsKey(x.Fingerprint)).ToArray();
                _machines = current;
                return applied;
            }
        }
    }

    private IReadOnlyList<PairedMachine> RefreshLocked()
    {
        if (_file is null || !_file.HasChanged())
            return [];
        Dictionary<string, PairedMachine> fresh;
        try
        {
            fresh = ReadFile();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException or JsonException)
        {
            // A file that cannot be read now (being replaced, damaged) leaves the list as it was; the next change retries.
            return [];
        }
        PairedMachine[] removed = _machines.Values.Where(x => !fresh.ContainsKey(x.Fingerprint)).ToArray();
        _machines = fresh;
        return removed;
    }

    private void Raise(IReadOnlyList<PairedMachine> removed, bool rethrow = false)
    {
        Action<PairedMachine>? handler = Revoked;
        if (handler is null)
            return;
        foreach (PairedMachine machine in removed)
        {
            if (rethrow)
            {
                handler(machine);
                continue;
            }
            try
            {
                handler(machine);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
        }
    }

    private Dictionary<string, PairedMachine> ReadFile()
    {
        var machines = new Dictionary<string, PairedMachine>(StringComparer.Ordinal);
        byte[]? json = _file!.Read();
        if (json is null)
            return machines;
        PairedMachine[] list = JsonSerializer.Deserialize<PairedMachine[]>(json) ?? [];
        foreach (PairedMachine machine in list)
        {
            if (machine.PublicKey is not { Length: IdentityKey.PublicKeySize } || KeyFingerprint.Compute(machine.PublicKey) != machine.Fingerprint)
                throw new InvalidDataException("Paired machine file holds an invalid entry.");
            machines[machine.Fingerprint] = machine;
        }
        return machines;
    }

    private void WriteFile(Dictionary<string, PairedMachine> machines)
        => _file!.Write(JsonSerializer.SerializeToUtf8Bytes(machines.Values.ToArray()));
}
