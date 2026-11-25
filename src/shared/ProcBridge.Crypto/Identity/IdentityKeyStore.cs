using System.Security.Cryptography;
using ProcBridge.Crypto.Identity.Interfaces;

namespace ProcBridge.Crypto.Identity;

/// <summary>
/// Persists one <see cref="IdentityKey"/> in a file, private key wrapped by an <see cref="IKeyProtector"/>.
/// File layout: magic "PBIK", format version byte, protected private key.
/// The protector is borrowed, not owned.
/// </summary>
public sealed class IdentityKeyStore
{
    private const byte FormatVersion = 1;
    private static readonly byte[] _magic = "PBIK"u8.ToArray();

    private readonly string _path;
    private readonly IKeyProtector _protector;

    public IdentityKeyStore(string path, IKeyProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public bool Exists => File.Exists(_path);

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
            var tempPath = WriteTempFile(key);
            try
            {
                File.Move(tempPath, _path, overwrite: false);
                created = true;
            }
            catch (IOException) when (File.Exists(_path))
            {
                // Another process created the key first; keep theirs.
                File.Delete(tempPath);
                created = false;
            }
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

    /// <exception cref="InvalidDataException">File is not a ProcBridge identity file.</exception>
    /// <exception cref="CryptographicException">Protected blob cannot be unwrapped (other user/machine, corrupted).</exception>
    public IdentityKey Load()
    {
        var data = File.ReadAllBytes(_path);
        if (data.Length <= _magic.Length + 1 || !data.AsSpan(0, _magic.Length).SequenceEqual(_magic))
            throw new InvalidDataException("Not a ProcBridge identity key file.");
        if (data[_magic.Length] != FormatVersion)
            throw new InvalidDataException($"Unsupported identity key file version {data[_magic.Length]}.");
        var privateKey = _protector.Unprotect(data[(_magic.Length + 1)..]);
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
        File.Move(WriteTempFile(key), _path, overwrite: true);
    }

    /// <summary>Writes the protected key to a unique temp file next to the target and returns its path.</summary>
    private string WriteTempFile(IdentityKey key)
    {
        var privateKey = key.ExportPrivateKey();
        byte[] blob;
        try
        {
            blob = _protector.Protect(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(_magic);
            stream.WriteByte(FormatVersion);
            stream.Write(blob);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
        return tempPath;
    }
}
