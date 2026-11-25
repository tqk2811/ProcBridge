using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>Machine overview (<c>Sys.Info</c>). Read only.</summary>
public sealed class SystemInfoProvider : ExplorerProviderBase
{
    internal SystemInfoProvider(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
        : base(policy, requesterId, audit)
    {
    }

    public SystemInfoResponse Get(SystemInfoRequest request) => Read(ExplorerPermissions.SystemInfo, () =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new SystemInfoResponse
        {
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.Version.ToString(),
            Is64BitOs = Environment.Is64BitOperatingSystem,
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            AgentUser = identity.Name,
            UptimeMilliseconds = Environment.TickCount64,
            Networks = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => new NetworkInterfaceEntry
                {
                    Name = n.Name,
                    Description = n.Description,
                    Addresses = [.. n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString())],
                })
                .ToList(),
        };
    });
}
