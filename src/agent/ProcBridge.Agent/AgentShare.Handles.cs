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
    // Ids are unique in the share, not per connection: a commit names the held handle of its writer while the handle
    // itself lives on another connection (the one RemoteHolds keeps).
    private readonly Dictionary<ulong, HeldFile> _heldById = [];
    private long _nextHeldId;

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
        var held = new HeldFile(this, (ulong)Interlocked.Increment(ref _nextHeldId), path, handle, write);
        lock (_held)
        {
            if (!_held.TryGetValue(path, out List<HeldFile>? list))
                _held[path] = list = [];
            list.Add(held);
            _heldById[held.Id] = held;
        }
        return held;
    }

    /// <summary>
    /// The handle held for writing <paramref name="path"/> (full path) a commit goes through, acquired (the caller
    /// releases it): the one of <paramref name="holdId"/> when it still holds that path, else any writer, else null.
    /// </summary>
    private HeldFile? AcquireHeldWriter(string path, ulong holdId)
    {
        lock (_held)
        {
            if (!_held.TryGetValue(path, out List<HeldFile>? list))
                return null;
            if (holdId != 0 && _heldById.TryGetValue(holdId, out HeldFile? named) && named.Write && list.Contains(named) && named.TryAcquire())
                return named;
            foreach (HeldFile held in list)
            {
                if (held.Write && held.TryAcquire())
                    return held;
            }
            return null;
        }
    }

    /// <summary>Other handles held on <paramref name="held"/>'s path (their locks bind a write through it).</summary>
    private HeldFile[] OtherHeld(HeldFile held)
    {
        lock (_held)
            return _held.TryGetValue(held.Path, out List<HeldFile>? list) ? list.Where(x => x != held).ToArray() : [];
    }

    private void Release(HeldFile held)
    {
        lock (_held)
        {
            if (_held.TryGetValue(held.Path, out List<HeldFile>? list) && list.Remove(held) && list.Count == 0)
                _held.Remove(held.Path);
            _heldById.Remove(held.Id);
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

    /// <summary>
    /// Writes the staged upload into the held handle (the commit of a file held open, in place). Not atomic: the caller
    /// remembers that the upload started writing, so a commit repeated after a failure writes it whole again. A range
    /// locked by another held handle (or a shared lock of this one) refuses the commit with
    /// <see cref="NtStatus.LockNotGranted"/> before anything is written; the write-back tries again later.
    /// </summary>
    private void CommitInPlace(HeldFile held, string staging, long size, long? lastWriteTime)
    {
        long end = Math.Max(size, RandomAccess.GetLength(held.Handle));
        if (held.HasLockBlockingWrite(0, end, own: true) || OtherHeld(held).Any(x => x.HasLockBlockingWrite(0, end, own: false)))
            throw new ProtocolStatusException(NtStatus.LockNotGranted, "A locked range of the file blocks the commit; retried later.");
        using (SafeFileHandle source = File.OpenHandle(staging, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (RandomAccess.GetLength(source) < size)
                throw new ProtocolStatusException(NtStatus.UnexpectedNetworkError, "The upload is incomplete.");
            byte[] buffer = new byte[256 * 1024];
            long offset = 0;
            while (offset < size)
            {
                int read = RandomAccess.Read(source, buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - offset)), offset);
                if (read == 0)
                    throw new ProtocolStatusException(NtStatus.UnexpectedNetworkError, "The upload is incomplete.");
                try
                {
                    RandomAccess.Write(held.Handle, buffer.AsSpan(0, read), offset);
                }
                catch (IOException e) when ((e.HResult & 0xFFFF) == ErrorLockViolation)
                {
                    // Locked by a user of machine B (not seen in the held handles).
                    throw new ProtocolStatusException(NtStatus.LockNotGranted, "A locked range of the file blocks the commit; retried later.");
                }
                offset += read;
            }
        }
        RandomAccess.SetLength(held.Handle, size);
        if (lastWriteTime is { } time)
            File.SetLastWriteTimeUtc(held.Handle, DateTime.FromFileTimeUtc(time));
    }

    /// <summary>
    /// A handle machine B keeps for an open on machine A; disposing it releases its locks. A commit writing through it
    /// holds a reference (<see cref="TryAcquire"/>): <see cref="Dispose"/> waits for it before closing the handle.
    /// </summary>
    public sealed class HeldFile : IDisposable
    {
        private readonly AgentShare _share;
        private readonly object _gate = new();
        private readonly List<(long Offset, long Length, bool Exclusive)> _locks = [];
        private int _references;
        private bool _disposed;

        internal HeldFile(AgentShare share, ulong id, string path, SafeFileHandle handle, bool write)
        {
            _share = share;
            Id = id;
            Path = path;
            Handle = handle;
            Write = write;
        }

        /// <summary>Id of the handle in its share (FsHandleResponse.HandleId).</summary>
        public ulong Id { get; }

        internal string Path { get; set; }

        internal SafeFileHandle Handle { get; }

        internal bool Write { get; }

        /// <summary>Takes a reference keeping the handle open; false once disposing.</summary>
        internal bool TryAcquire()
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;
                _references++;
                return true;
            }
        }

        internal void ReleaseReference()
        {
            lock (_gate)
            {
                if (--_references == 0)
                    Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// A lock of this handle that a write of [<paramref name="offset"/>, <paramref name="end"/>) runs into: any lock
        /// of another handle, or a shared one of the writing handle itself (<paramref name="own"/>).
        /// </summary>
        internal bool HasLockBlockingWrite(long offset, long end, bool own)
        {
            lock (_gate)
                return _locks.Any(x => (!own || !x.Exclusive) && x.Offset < end && offset < x.Offset + Math.Max(x.Length, 1));
        }

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
                {
                    lock (_gate)
                        _locks.Add((offset, length, exclusive));
                    return;
                }
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
            {
                lock (_gate)
                {
                    int index = _locks.FindIndex(x => x.Offset == offset && x.Length == length);
                    if (index >= 0)
                        _locks.RemoveAt(index);
                }
                return;
            }
            int error = Marshal.GetLastPInvokeError();
            throw new ProtocolStatusException(error == ErrorNotLocked ? NtStatus.RangeNotLocked : NtStatus.Unsuccessful, $"UnlockFileEx failed: {error}.");
        }

        /// <summary>Closes the handle once the commits writing through it are done (waits for them).</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                while (_references > 0)
                    Monitor.Wait(_gate);
                _locks.Clear();
            }
            _share.Release(this);
            Handle.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint lengthLow, uint lengthHigh, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnlockFileEx(SafeFileHandle file, uint reserved, uint lengthLow, uint lengthHigh, ref NativeOverlapped overlapped);
}
