using System.Security.Cryptography;
using ProcBridge.Crypto.Identity.Interfaces;

namespace ProcBridge.Crypto.Storage;

/// <summary>
/// A small file whose content is wrapped by an <see cref="IKeyProtector"/> (DPAPI in production) and that several
/// instances, possibly in different processes, share. On-disk layout: <c>magic | version byte | protected body</c>.
/// Shared by every DPAPI-protected store (identity key, paired machines, Explorer policies) so they agree on the
/// header check, the atomic write (temp file then move) and the corrupt-file errors. Gives its owners an
/// inter-process lock (<see cref="Lock"/>, a <c>.lock</c> file opened with <see cref="FileShare.None"/>), a cheap
/// change check (<see cref="HasChanged"/>, compares creation time, write time and size), an atomic replace
/// (<see cref="Write"/>) and an atomic first-writer-wins create (<see cref="TryCreate"/>). The owner serialises its own
/// threads; the lock only orders processes. The protector is borrowed, not owned.
/// </summary>
public sealed class ProtectedStoreFile
{
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(15);

    private readonly byte[] _magic;
    private readonly byte _version;
    private readonly IKeyProtector _protector;
    private (bool Exists, long Length, long Created, long Written) _stamp = (false, 0, 0, 0);

    public ProtectedStoreFile(string path, ReadOnlySpan<byte> magic, byte version, IKeyProtector protector)
    {
        Path = System.IO.Path.GetFullPath(path);
        _magic = magic.ToArray();
        _version = version;
        _protector = protector;
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>Takes the inter-process lock of this file; dispose to release. Waits up to 15 seconds.</summary>
    /// <exception cref="IOException">Another process kept the lock too long.</exception>
    public IDisposable Lock()
    {
        CreateDirectory();
        DateTime deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                // A lock file being deleted or held by another user's handle reports access denied for a moment.
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>True when the file differs from the last one this instance read or wrote (or was created or deleted).</summary>
    public bool HasChanged() => Stamp() != _stamp;

    /// <summary>
    /// Reads and unwraps the body of the file and remembers its stamp; null when the file does not exist. Retries while
    /// another process is replacing it. The caller owns the returned plaintext (zero it when it is secret).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a file of this kind or another version.</exception>
    /// <exception cref="CryptographicException">The body cannot be unwrapped (other user or machine, corrupted).</exception>
    public byte[]? Read()
    {
        for (int attempt = 0; ; attempt++)
        {
            var stamp = Stamp();
            if (!stamp.Exists)
            {
                _stamp = stamp;
                return null;
            }
            byte[] data;
            try
            {
                data = File.ReadAllBytes(Path);
            }
            catch (Exception e) when (attempt < 50 && e is IOException and not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(10);
                continue;
            }
            catch (FileNotFoundException)
            {
                // Deleted between the stamp and the read: look again.
                if (attempt >= 50)
                    throw;
                continue;
            }
            if (data.Length <= _magic.Length + 1 || !data.AsSpan(0, _magic.Length).SequenceEqual(_magic))
                throw new InvalidDataException($"Not a ProcBridge store file: {System.IO.Path.GetFileName(Path)}.");
            if (data[_magic.Length] != _version)
                throw new InvalidDataException($"Unsupported store file version {data[_magic.Length]}.");
            // Remembered before unwrapping: a body that cannot be unwrapped is not retried until the file changes again.
            _stamp = stamp;
            return _protector.Unprotect(data[(_magic.Length + 1)..]);
        }
    }

    /// <summary>Wraps and writes atomically (temp file then replace) so a crash never leaves a half-written file; remembers the new stamp. Call under <see cref="Lock"/> when the file is shared.</summary>
    public void Write(byte[] plaintext)
    {
        string tempPath = WriteTempFile(plaintext);
        try
        {
            File.Move(tempPath, Path, overwrite: true);
            _stamp = Stamp();
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Wraps and publishes the file only when it does not exist yet (no-overwrite move), so when several processes race
    /// exactly one wins. False when another writer created the file first; its content is kept.
    /// </summary>
    public bool TryCreate(byte[] plaintext)
    {
        string tempPath = WriteTempFile(plaintext);
        try
        {
            File.Move(tempPath, Path, overwrite: false);
            _stamp = Stamp();
            return true;
        }
        catch (IOException) when (File.Exists(Path))
        {
            File.Delete(tempPath);
            return false;
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>Writes the header and wrapped body to a unique temp file next to the target and returns its path.</summary>
    private string WriteTempFile(byte[] plaintext)
    {
        byte[] blob = _protector.Protect(plaintext);
        CreateDirectory();
        string tempPath = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(_magic);
            stream.WriteByte(_version);
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

    private void CreateDirectory()
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private (bool Exists, long Length, long Created, long Written) Stamp()
    {
        var info = new FileInfo(Path);
        return info.Exists ? (true, info.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks) : (false, 0, 0, 0);
    }
}
