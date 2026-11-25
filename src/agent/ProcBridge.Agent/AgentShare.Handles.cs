using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Agent;

/// <summary>
/// Handles machine B keeps for opens on machine A (M8): byte range locks forwarded by the hook are taken on them, and
/// their share mode keeps local users out the way the open on machine A would. A commit of a file held for writing
/// goes through the held handle, in place, so the file keeps its identity and its locks.
/// </summary>
public sealed partial class AgentShare
{
    private const uint LockfileFailImmediately = 0x1;
    private const uint LockfileExclusiveLock = 0x2;
    private const int ErrorLockViolation = 33;
    private const int ErrorNotLocked = 158;

    private readonly Dictionary<string, List<HeldFile>> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opens <paramref name="relativePath"/> and keeps the handle until <see cref="HeldFile.Dispose"/>. Delete sharing is
    /// always granted: renames and deletes from machine A go through this Agent. A local user holding the file in a way
    /// the share mode refuses fails the open with <see cref="NtStatus.SharingViolation"/>.
    /// </summary>
    public HeldFile OpenHeld(string relativePath, uint shareAccess, bool write)
    {
        if (write)
            EnsureWritable();
        string path = ResolveChild(relativePath);
        var share = (FileShare)(shareAccess & 0x3) | FileShare.Delete;
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, share);
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or ErrorLockViolation)
        {
            throw new ProtocolStatusException(NtStatus.SharingViolation, "The file is in use on machine B.");
        }
        if (GetFileType(handle) != FileTypeDisk)
        {
            handle.Dispose();
            throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Not a disk file.");
        }
        var held = new HeldFile(this, path, handle, write);
        lock (_held)
        {
            if (!_held.TryGetValue(path, out List<HeldFile>? list))
                _held[path] = list = [];
            list.Add(held);
        }
        return held;
    }

    /// <summary>A handle held for writing <paramref name="path"/> (full path), or null.</summary>
    private HeldFile? FindHeldWriter(string path)
    {
        lock (_held)
            return _held.TryGetValue(path, out List<HeldFile>? list) ? list.FirstOrDefault(x => x.Write) : null;
    }

    private void Release(HeldFile held)
    {
        lock (_held)
        {
            if (_held.TryGetValue(held.Path, out List<HeldFile>? list) && list.Remove(held) && list.Count == 0)
                _held.Remove(held.Path);
        }
    }

    /// <summary>Held handles follow a file renamed through this Agent.</summary>
    private void RenameHeld(string source, string target)
    {
        lock (_held)
        {
            if (!_held.Remove(source, out List<HeldFile>? list))
                return;
            foreach (HeldFile held in list)
                held.Path = target;
            _held[target] = list;
        }
    }

    /// <summary>Writes the staged upload into the held handle (the commit of a file held open, in place).</summary>
    private static void CommitInPlace(HeldFile held, string staging, long size, long? lastWriteTime)
    {
        using (SafeFileHandle source = File.OpenHandle(staging, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            byte[] buffer = new byte[256 * 1024];
            long offset = 0;
            while (offset < size)
            {
                int read = RandomAccess.Read(source, buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - offset)), offset);
                if (read == 0)
                    break;
                RandomAccess.Write(held.Handle, buffer.AsSpan(0, read), offset);
                offset += read;
            }
        }
        RandomAccess.SetLength(held.Handle, size);
        if (lastWriteTime is { } time)
            File.SetLastWriteTimeUtc(held.Handle, DateTime.FromFileTimeUtc(time));
    }

    /// <summary>A handle machine B keeps for an open on machine A; disposing it releases its locks.</summary>
    public sealed class HeldFile : IDisposable
    {
        private readonly AgentShare _share;
        private int _disposed;

        internal HeldFile(AgentShare share, string path, SafeFileHandle handle, bool write)
        {
            _share = share;
            Path = path;
            Handle = handle;
            Write = write;
        }

        internal string Path { get; set; }

        internal SafeFileHandle Handle { get; }

        internal bool Write { get; }

        /// <summary>
        /// Locks [<paramref name="offset"/>, +<paramref name="length"/>). A conflicting lock is retried until
        /// <paramref name="wait"/> runs out, then fails with <see cref="NtStatus.LockNotGranted"/>.
        /// </summary>
        public async Task LockAsync(long offset, long length, bool exclusive, TimeSpan wait, CancellationToken cancellationToken)
        {
            long deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
            while (true)
            {
                if (TryLock(offset, length, exclusive))
                    return;
                if (Environment.TickCount64 >= deadline)
                    throw new ProtocolStatusException(NtStatus.LockNotGranted, "The range is locked by another open.");
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        private bool TryLock(long offset, long length, bool exclusive)
        {
            var overlapped = new NativeOverlapped { OffsetLow = (int)offset, OffsetHigh = (int)(offset >> 32) };
            uint flags = LockfileFailImmediately | (exclusive ? LockfileExclusiveLock : 0);
            if (LockFileEx(Handle, flags, 0, (uint)length, (uint)(length >> 32), ref overlapped))
                return true;
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorLockViolation)
                return false;
            throw new ProtocolStatusException(error == 87 ? NtStatus.InvalidParameter : NtStatus.Unsuccessful, $"LockFileEx failed: {error}.");
        }

        public void Unlock(long offset, long length)
        {
            var overlapped = new NativeOverlapped { OffsetLow = (int)offset, OffsetHigh = (int)(offset >> 32) };
            if (UnlockFileEx(Handle, 0, (uint)length, (uint)(length >> 32), ref overlapped))
                return;
            int error = Marshal.GetLastPInvokeError();
            throw new ProtocolStatusException(error == ErrorNotLocked ? NtStatus.RangeNotLocked : NtStatus.Unsuccessful, $"UnlockFileEx failed: {error}.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _share.Release(this);
            Handle.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint lengthLow, uint lengthHigh, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnlockFileEx(SafeFileHandle file, uint reserved, uint lengthLow, uint lengthHigh, ref NativeOverlapped overlapped);
}
