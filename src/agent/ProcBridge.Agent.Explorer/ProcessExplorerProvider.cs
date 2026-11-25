using System.Diagnostics;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Explorer.Native;

namespace ProcBridge.Agent.Explorer;

/// <summary>Process list and kill (<c>Proc.*</c>).</summary>
public sealed class ProcessExplorerProvider : ExplorerProviderBase
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    internal ProcessExplorerProvider(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
        : base(policy, requesterId, audit)
    {
    }

    public ProcessListResponse List(ProcessListRequest request) => Read(ExplorerPermissions.ProcessRead, () =>
    {
        var entries = new List<ProcessEntry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                entries.Add(ToEntry(process));
        }

        return new ProcessListResponse { Processes = entries };
    });

    public ExplorerAckResponse Kill(ProcessKillRequest request) => Write(ExplorerPermissions.ProcessKill, "Proc.Kill", request.Pid.ToString(), () =>
    {
        if (request.Pid <= 4)
            throw new ExplorerDeniedException(ExplorerStatus.AccessDenied, "System processes cannot be killed.");
        if (request.Pid == Environment.ProcessId)
            throw new ExplorerDeniedException(ExplorerStatus.AccessDenied, "The Agent cannot kill itself.");

        Process process;
        try
        {
            process = Process.GetProcessById(request.Pid);
        }
        catch (ArgumentException)
        {
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Process not found.");
        }

        using (process)
        {
            process.Kill(request.EntireTree);
            process.WaitForExit(KillWait);
        }
    });

    private static ProcessEntry ToEntry(Process process)
    {
        int parentPid = 0;
        string? imagePath = null;
        using (var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, process.Id))
        {
            if (!handle.IsInvalid)
            {
                parentPid = QueryParentPid(handle);
                imagePath = QueryImagePath(handle);
            }
        }

        return new ProcessEntry
        {
            Pid = process.Id,
            ParentPid = parentPid,
            Name = process.ProcessName,
            ImagePath = imagePath,
            SessionId = Try(() => process.SessionId),
            WorkingSetBytes = Try(() => process.WorkingSet64),
            StartTimeUtc = Try<DateTime?>(() => process.StartTime.ToUniversalTime()),
        };
    }

    private static unsafe int QueryParentPid(Microsoft.Win32.SafeHandles.SafeProcessHandle handle)
    {
        NativeMethods.ProcessBasicInformation info;
        var status = NativeMethods.NtQueryInformationProcess(handle, 0, &info, (uint)sizeof(NativeMethods.ProcessBasicInformation), out _);
        return status >= 0 ? (int)info.InheritedFromUniqueProcessId : 0;
    }

    private static unsafe string? QueryImagePath(Microsoft.Win32.SafeHandles.SafeProcessHandle handle)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        fixed (char* p = buffer)
        {
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, p, ref size))
                return null;
        }

        return new string(buffer, 0, (int)size);
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return default;
        }
    }
}
