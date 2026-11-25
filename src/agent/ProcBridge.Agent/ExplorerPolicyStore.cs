using System.Security.Cryptography;
using MessagePack;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Crypto.Identity.Interfaces;
using ProcBridge.Crypto.Storage;
using ProcBridge.Protocol;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent;

/// <summary>
/// Remote Explorer permission profiles set by the owner of machine B, one per paired machine A, by fingerprint
/// (plan section 14). A machine without a profile gets <see cref="ExplorerPolicy"/> defaults: no permission at all.
/// Persisted as MessagePack wrapped by an <see cref="IKeyProtector"/> (DPAPI) so other users cannot widen a profile;
/// the parameterless constructor keeps profiles in memory (tests). A session copies its profile when its connection
/// opens, so a change must close the open connections of that machine: <see cref="Changed"/> tells the Agent which
/// fingerprint to drop, and the next connection reads the new profile. Like <see cref="PairedMachineStore"/> the file
/// is the truth: reads reload it when another instance or process changed it (raising <see cref="Changed"/>), writes
/// take an inter-process lock and apply only their own change to the current file. Thread-safe; the protector is borrowed.
/// </summary>
public sealed class ExplorerPolicyStore
{
    private const byte FormatVersion = 1;
    private static readonly byte[] _magic = "PBEP"u8.ToArray();

    private readonly ProtectedStoreFile? _file;
    private readonly Lock _lock = new();
    private Dictionary<string, ExplorerPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>In-memory store.</summary>
    public ExplorerPolicyStore()
    {
    }

    /// <exception cref="InvalidDataException">The file is not a policy file.</exception>
    /// <exception cref="CryptographicException">The file cannot be unwrapped (other user, corrupted).</exception>
    public ExplorerPolicyStore(string path, IKeyProtector protector)
    {
        _file = new ProtectedStoreFile(path, _magic, FormatVersion, protector);
        _policies = ReadFile();
    }

    /// <summary>
    /// Raised with the fingerprint whose profile was set, changed or removed (here, or by another instance or process
    /// that changed the file), outside the store lock. A handler that throws is ignored for changes found by a reload.
    /// </summary>
    public event Action<string>? Changed;

    /// <summary>Copy of the profile of <paramref name="fingerprint"/>, or an empty profile (nothing allowed).</summary>
    public ExplorerPolicy Get(string fingerprint)
    {
        ExplorerPolicy result;
        IReadOnlyList<string> changed;
        lock (_lock)
        {
            changed = RefreshLocked();
            result = _policies.TryGetValue(fingerprint, out ExplorerPolicy? policy) ? Copy(policy) : new ExplorerPolicy();
        }
        Raise(changed);
        return result;
    }

    /// <summary>Reloads the file when another instance or process changed it, raising <see cref="Changed"/> for each profile that differs.</summary>
    public void Refresh()
    {
        IReadOnlyList<string> changed;
        lock (_lock)
            changed = RefreshLocked();
        Raise(changed);
    }

    public void Set(string fingerprint, ExplorerPolicy policy)
    {
        ExplorerPolicy copy = Copy(policy);
        Mutate(policies =>
        {
            policies[fingerprint] = copy;
            return true;
        }, out IReadOnlyList<string> changed);
        Raise(changed.Append(fingerprint).Distinct().ToArray(), rethrow: true);
    }

    public bool Remove(string fingerprint)
    {
        bool removed = Mutate(policies => policies.Remove(fingerprint), out IReadOnlyList<string> changed);
        Raise(changed, rethrow: true);
        return removed;
    }

    private static ExplorerPolicy Copy(ExplorerPolicy policy) => new()
    {
        FileRoots = policy.FileRoots.ToArray(),
        RegistryRoots = policy.RegistryRoots.ToArray(),
        Permissions = policy.Permissions,
    };

    private bool Mutate(Func<Dictionary<string, ExplorerPolicy>, bool> change, out IReadOnlyList<string> changed)
    {
        lock (_lock)
        {
            if (_file is null)
            {
                Dictionary<string, ExplorerPolicy> before = _policies;
                _policies = new Dictionary<string, ExplorerPolicy>(before, StringComparer.Ordinal);
                bool applied = change(_policies);
                changed = Diff(before, _policies);
                return applied;
            }

            using (_file.Lock())
            {
                Dictionary<string, ExplorerPolicy> current = ReadFile();
                bool applied = change(current);
                if (applied)
                    WriteFile(current);
                changed = Diff(_policies, current);
                _policies = current;
                return applied;
            }
        }
    }

    private IReadOnlyList<string> RefreshLocked()
    {
        if (_file is null || !_file.HasChanged())
            return [];
        Dictionary<string, ExplorerPolicy> fresh;
        try
        {
            fresh = ReadFile();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException)
        {
            // A file that cannot be read now (being replaced, damaged) leaves the profiles as they were; the next change retries.
            return [];
        }
        IReadOnlyList<string> changed = Diff(_policies, fresh);
        _policies = fresh;
        return changed;
    }

    /// <summary>Fingerprints that are in only one of the two maps or whose profile differs.</summary>
    private static string[] Diff(Dictionary<string, ExplorerPolicy> before, Dictionary<string, ExplorerPolicy> after)
    {
        var changed = new List<string>();
        foreach ((string fingerprint, ExplorerPolicy policy) in before)
        {
            if (!after.TryGetValue(fingerprint, out ExplorerPolicy? other) || !Serialize(policy).AsSpan().SequenceEqual(Serialize(other)))
                changed.Add(fingerprint);
        }
        changed.AddRange(after.Keys.Where(x => !before.ContainsKey(x)));
        return changed.ToArray();
    }

    private static byte[] Serialize(ExplorerPolicy policy) => MessagePackSerializer.Serialize(policy, ProtocolSerializer.Options);

    private void Raise(IReadOnlyList<string> changed, bool rethrow = false)
    {
        Action<string>? handler = Changed;
        if (handler is null)
            return;
        foreach (string fingerprint in changed)
        {
            if (rethrow)
            {
                handler(fingerprint);
                continue;
            }
            try
            {
                handler(fingerprint);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
        }
    }

    private Dictionary<string, ExplorerPolicy> ReadFile()
    {
        byte[]? plain = _file!.Read();
        if (plain is null)
            return new Dictionary<string, ExplorerPolicy>(StringComparer.Ordinal);
        try
        {
            return new Dictionary<string, ExplorerPolicy>(
                MessagePackSerializer.Deserialize<Dictionary<string, ExplorerPolicy>>(plain, ProtocolSerializer.Options), StringComparer.Ordinal);
        }
        catch (MessagePackSerializationException e)
        {
            throw new InvalidDataException("Explorer policy file is corrupted.", e);
        }
    }

    private void WriteFile(Dictionary<string, ExplorerPolicy> policies)
        => _file!.Write(MessagePackSerializer.Serialize(policies, ProtocolSerializer.Options));
}
