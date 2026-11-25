using System.Security.Cryptography;
using MessagePack;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Crypto.Identity.Interfaces;
using ProcBridge.Protocol;

namespace ProcBridge.Agent;

/// <summary>
/// Remote Explorer permission profiles set by the owner of machine B, one per paired machine A, by fingerprint
/// (plan section 14). A machine without a profile gets <see cref="ExplorerPolicy"/> defaults: no permission at all.
/// Persisted as MessagePack wrapped by an <see cref="IKeyProtector"/> (DPAPI) so other users cannot widen a profile;
/// the parameterless constructor keeps profiles in memory (tests). Profiles are read when a connection opens, so a
/// change applies to new connections. Thread-safe; the protector is borrowed.
/// </summary>
public sealed class ExplorerPolicyStore
{
    private const byte FormatVersion = 1;
    private static readonly byte[] _magic = "PBEP"u8.ToArray();

    private readonly string? _path;
    private readonly IKeyProtector? _protector;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ExplorerPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>In-memory store.</summary>
    public ExplorerPolicyStore()
    {
    }

    /// <exception cref="InvalidDataException">The file is not a policy file.</exception>
    /// <exception cref="CryptographicException">The file cannot be unwrapped (other user, corrupted).</exception>
    public ExplorerPolicyStore(string path, IKeyProtector protector)
    {
        _path = path;
        _protector = protector;
        if (File.Exists(path))
            Load();
    }

    /// <summary>Copy of the profile of <paramref name="fingerprint"/>, or an empty profile (nothing allowed).</summary>
    public ExplorerPolicy Get(string fingerprint)
    {
        lock (_lock)
            return _policies.TryGetValue(fingerprint, out ExplorerPolicy? policy) ? Copy(policy) : new ExplorerPolicy();
    }

    public void Set(string fingerprint, ExplorerPolicy policy)
    {
        lock (_lock)
        {
            _policies[fingerprint] = Copy(policy);
            SaveLocked();
        }
    }

    public bool Remove(string fingerprint)
    {
        lock (_lock)
        {
            if (!_policies.Remove(fingerprint))
                return false;
            SaveLocked();
            return true;
        }
    }

    private static ExplorerPolicy Copy(ExplorerPolicy policy) => new()
    {
        FileRoots = policy.FileRoots.ToArray(),
        RegistryRoots = policy.RegistryRoots.ToArray(),
        Permissions = policy.Permissions,
    };

    private void Load()
    {
        byte[] data = File.ReadAllBytes(_path!);
        if (data.Length <= _magic.Length + 1 || !data.AsSpan(0, _magic.Length).SequenceEqual(_magic))
            throw new InvalidDataException("Not a ProcBridge explorer policy file.");
        if (data[_magic.Length] != FormatVersion)
            throw new InvalidDataException($"Unsupported explorer policy file version {data[_magic.Length]}.");
        byte[] body = _protector!.Unprotect(data[(_magic.Length + 1)..]);
        Dictionary<string, ExplorerPolicy> policies;
        try
        {
            policies = MessagePackSerializer.Deserialize<Dictionary<string, ExplorerPolicy>>(body, ProtocolSerializer.Options);
        }
        catch (MessagePackSerializationException e)
        {
            throw new InvalidDataException("Explorer policy file is corrupted.", e);
        }
        foreach ((string fingerprint, ExplorerPolicy policy) in policies)
            _policies[fingerprint] = policy;
    }

    /// <summary>Atomic write (temp file then replace).</summary>
    private void SaveLocked()
    {
        if (_path is null)
            return;
        byte[] blob = _protector!.Protect(MessagePackSerializer.Serialize(_policies, ProtocolSerializer.Options));
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
