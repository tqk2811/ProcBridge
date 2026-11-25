using System.Security.Cryptography;
using ProcBridge.Crypto.Identity.Interfaces;
using ProcBridge.Crypto.Storage;

namespace ProcBridge.Crypto.Identity;

/// <summary>
/// Persists one <see cref="IdentityKey"/> in a file, private key wrapped by an <see cref="IKeyProtector"/>.
/// File layout (see <see cref="ProtectedStoreFile"/>): magic "PBIK", format version byte, protected private key.
/// The protector is borrowed, not owned.
/// </summary>
public sealed class IdentityKeyStore
{
    private const byte FormatVersion = 1;
    private static readonly byte[] _magic = "PBIK"u8.ToArray();

    private readonly ProtectedStoreFile _file;

    public IdentityKeyStore(string path, IKeyProtector protector)
    {
        _file = new ProtectedStoreFile(path, _magic, FormatVersion, protector);
    }

    public bool Exists => _file.Exists;

    /// <summary>
    /// Loads the stored key, or generates and saves a new one on first run. Safe when several
    /// processes start at once: the new file is published with a no-overwrite move, so exactly one
    /// key wins and the losers load it instead of replacing it.
    /// </summary>
    public IdentityKey LoadOrCreate()
    {
        if (Exists)
            return Load();
        var key = IdentityKey.Generate();
        bool created;
        try
        {
            // Another process may create the key first; then keep theirs.
            created = WithPrivateKey(key, _file.TryCreate);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        if (created)
            return key;
        key.Dispose();
        return Load();
    }

    /// <exception cref="FileNotFoundException">No key file yet.</exception>
    /// <exception cref="InvalidDataException">File is not a ProcBridge identity file.</exception>
    /// <exception cref="CryptographicException">Protected blob cannot be unwrapped (other user/machine, corrupted).</exception>
    public IdentityKey Load()
    {
        var privateKey = _file.Read() ?? throw new FileNotFoundException("Identity key file not found.", _file.Path);
        try
        {
            return IdentityKey.FromPrivateKey(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>Writes atomically (temp file then replace) so a crash never leaves a half-written key.</summary>
    public void Save(IdentityKey key)
    {
        WithPrivateKey(key, plaintext =>
        {
            _file.Write(plaintext);
            return true;
        });
    }

    /// <summary>Runs <paramref name="action"/> on an exported copy of the private key and zeroes the copy afterwards.</summary>
    private static bool WithPrivateKey(IdentityKey key, Func<byte[], bool> action)
    {
        var privateKey = key.ExportPrivateKey();
        try
        {
            return action(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
