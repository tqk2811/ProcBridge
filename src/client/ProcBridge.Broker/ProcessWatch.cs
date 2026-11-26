using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Broker.Enums;
using ProcBridge.Broker.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Reports when hooked processes exit (HandleSharing-Design-vi.md section 5.4): one process handle and one registered wait
/// per watched process, the creation time checked against the <see cref="ProcessKey"/> so a reused process id is not taken
/// for the old process. Exits are delivered one at a time, in order, to the handler given to the constructor, on a task of
/// their own (never on the thread pool's wait thread, never under a caller's lock). A process stays watched until it
/// exits or the watch is disposed. Thread safe. Owns the process handles and waits.
/// </summary>
public sealed class ProcessWatch : IAsyncDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;
    private const uint WaitObject0 = 0;
    private const uint StillActive = 259;

    private readonly Func<ProcessKey, Task> _exited;
    private readonly Lock _lock = new();
    private readonly Dictionary<ProcessKey, Watched> _watched = [];
    private readonly Channel<ProcessKey> _exits = Channel.CreateUnbounded<ProcessKey>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _delivery;
    private bool _disposed;

    /// <param name="exited">Called once for each watched process that exited; failures are logged and ignored.</param>
    public ProcessWatch(Func<ProcessKey, Task> exited)
    {
        _exited = exited;
        _delivery = Task.Run(DeliverAsync);
    }

    /// <summary>Processes watched now (alive as far as the watch knows), for tests.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _watched.Count;
        }
    }

    /// <summary>True while <paramref name="key"/> is watched (its exit not reported yet).</summary>
    public bool IsWatching(ProcessKey key)
    {
        lock (_lock)
            return _watched.ContainsKey(key);
    }

    /// <summary>
    /// The key of process <paramref name="processId"/> as it runs now; null when it cannot be opened (gone, or access
    /// denied).
    /// </summary>
    public static ProcessKey? TryGetKey(int processId) => TryGetKey(processId, out _);

    /// <inheritdoc cref="TryGetKey(int)"/>
    /// <param name="accessDenied">True when the process could not be opened for lack of rights: it may still run.</param>
    public static ProcessKey? TryGetKey(int processId, out bool accessDenied)
    {
        accessDenied = false;
        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            accessDenied = Marshal.GetLastPInvokeError() == ErrorAccessDenied;
            return null;
        }
        return GetCreationTime(process) is { } creationTime ? new ProcessKey(processId, creationTime) : null;
    }

    /// <summary>
    /// Watches <paramref name="key"/>: its exit is reported once (right away, on the delivery task, when it is already
    /// gone). Watching a process again is a no-op. Call it after recording what the exit should release, so an exit that
    /// is reported at once finds it.
    /// </summary>
    public ProcessWatchResult Watch(ProcessKey key)
    {
        if (key.CreationTime == 0 || key.ProcessId <= 0)
            return ProcessWatchResult.Unavailable;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watched.ContainsKey(key))
                return ProcessWatchResult.Watching;
        }

        SafeProcessHandle process = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, key.ProcessId);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            process.Dispose();
            if (error == ErrorAccessDenied)
            {
                Trace.TraceWarning($"ProcBridge: cannot watch process {key.ProcessId} (access denied); what it holds is released with its last pipe connection or at the end of the session.");
                return ProcessWatchResult.Unavailable;
            }
            // No such process any more.
            _exits.Writer.TryWrite(key);
            return ProcessWatchResult.Exited;
        }
        if (GetCreationTime(process) != key.CreationTime)
        {
            // The id now belongs to another process: the one of the key exited.
            process.Dispose();
            _exits.Writer.TryWrite(key);
            return ProcessWatchResult.Exited;
        }

        // Exited, but something still holds its handle open. A process that ended with exit code STILL_ACTIVE (259) is
        // only told by its signaled handle; one being terminated has its exit code before its handle is signaled.
        if (WaitForSingleObject(process, 0) == WaitObject0 || (GetExitCodeProcess(process, out uint exitCode) && exitCode != StillActive))
        {
            process.Dispose();
            _exits.Writer.TryWrite(key);
            return ProcessWatchResult.Exited;
        }

        var watched = new Watched(process);
        lock (_lock)
        {
            if (_disposed || !_watched.TryAdd(key, watched))
            {
                // Disposed meanwhile, or another thread watched the same process first.
                watched.Dispose();
                ObjectDisposedException.ThrowIf(_disposed, this);
                return ProcessWatchResult.Watching;
            }
            watched.Registration = ThreadPool.RegisterWaitForSingleObject(watched.WaitHandle,
                (_, _) => _exits.Writer.TryWrite(key), null, Timeout.Infinite, executeOnlyOnce: true);
        }
        return ProcessWatchResult.Watching;
    }

    private async Task DeliverAsync()
    {
        await foreach (ProcessKey key in _exits.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Watched? watched;
            lock (_lock)
            {
                if (_watched.Remove(key, out watched))
                    watched.Unregister();
            }
            watched?.Dispose();
            try
            {
                await _exited(key).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Trace.TraceError($"ProcBridge: releasing what process {key.ProcessId} held failed: {e.Message}");
            }
        }
    }

    private static long? GetCreationTime(SafeProcessHandle process)
        => GetProcessTimes(process, out long creation, out _, out _, out _) ? creation : null;

    public async ValueTask DisposeAsync()
    {
        Watched[] watched;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            watched = _watched.Values.ToArray();
            _watched.Clear();
            foreach (Watched item in watched)
                item.Unregister();
        }
        foreach (Watched item in watched)
            item.Dispose();
        _exits.Writer.TryComplete();
        await _delivery.ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    /// <summary>A watched process: its handle (as a wait handle) and the registered wait.</summary>
    private sealed class Watched(SafeProcessHandle process) : IDisposable
    {
        public WaitHandle WaitHandle { get; } = new ProcessWaitHandle(process);

        /// <summary>Set under the watch's lock right after the entry is added.</summary>
        public RegisteredWaitHandle? Registration { get; set; }

        /// <summary>Under the watch's lock: no callback after this returns (one already running only posts the key).</summary>
        public void Unregister()
        {
            Registration?.Unregister(null);
            Registration = null;
        }

        public void Dispose() => WaitHandle.Dispose();
    }

    /// <summary>A process handle seen as a wait handle (signaled when the process exits). Owns the handle.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: true);
            process.SetHandleAsInvalid();
        }
    }
}
