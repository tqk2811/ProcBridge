namespace ProcBridge.Transport.Secure;

/// <summary>
/// A small protected file (<c>magic | version | body</c>) that several instances, possibly in different processes, share.
/// Gives its owners three things: an inter-process lock (<see cref="Lock"/>, a <c>.lock</c> file opened with
/// <see cref="FileShare.None"/>), a cheap change check (<see cref="HasChanged"/>, compares creation time, write time
/// and size) and an atomic write. The owner serialises its own threads; the lock only orders processes.
/// </summary>
public sealed class SharedStoreFile
{
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(15);

    private readonly byte[] _magic;
    private readonly byte _version;
    private (bool Exists, long Length, long Created, long Written) _stamp = (false, 0, 0, 0);

    public SharedStoreFile(string path, ReadOnlySpan<byte> magic, byte version)
    {
        Path = System.IO.Path.GetFullPath(path);
        _magic = magic.ToArray();
        _version = version;
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>Takes the inter-process lock of this file; dispose to release. Waits up to 15 seconds.</summary>
    /// <exception cref="IOException">Another process kept the lock too long.</exception>
    public IDisposable Lock()
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
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
    /// Reads the body of the file and remembers its stamp; null when the file does not exist. Retries while another
    /// process is replacing it.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a file of this kind or another version.</exception>
    public byte[]? ReadBody()
    {
        for (int attempt = 0; ; attempt++)
        {
            var stamp = Stamp();
            if (!stamp.Exists)
            {
                _stamp = stamp;
                return null;
            }
            try
            {
                byte[] data = File.ReadAllBytes(Path);
                if (data.Length <= _magic.Length + 1 || !data.AsSpan(0, _magic.Length).SequenceEqual(_magic))
                    throw new InvalidDataException($"Not a ProcBridge store file: {System.IO.Path.GetFileName(Path)}.");
                if (data[_magic.Length] != _version)
                    throw new InvalidDataException($"Unsupported store file version {data[_magic.Length]}.");
                _stamp = stamp;
                return data[(_magic.Length + 1)..];
            }
            catch (Exception e) when (attempt < 50 && e is IOException and not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(10);
            }
            catch (FileNotFoundException)
            {
                // Deleted between the stamp and the read: look again.
                if (attempt >= 50)
                    throw;
            }
        }
    }

    /// <summary>Atomic write (temp file then replace) so a crash never leaves a half-written file; remembers the new stamp. Call under <see cref="Lock"/>.</summary>
    public void WriteBody(ReadOnlySpan<byte> body)
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string tempPath = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(_magic);
                stream.WriteByte(_version);
                stream.Write(body);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, Path, overwrite: true);
            _stamp = Stamp();
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    private (bool Exists, long Length, long Created, long Written) Stamp()
    {
        var info = new FileInfo(Path);
        return info.Exists ? (true, info.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks) : (false, 0, 0, 0);
    }
}
