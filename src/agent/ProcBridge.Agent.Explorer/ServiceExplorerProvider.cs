using System.ServiceProcess;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>Windows services: list and start/stop/restart (<c>Sys.Services</c>).</summary>
public sealed class ServiceExplorerProvider : ExplorerProviderBase
{
    private static readonly TimeSpan StatusWait = TimeSpan.FromSeconds(30);

    internal ServiceExplorerProvider(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
        : base(policy, requesterId, audit)
    {
    }

    public ServiceListResponse List(ServiceListRequest request) => Read(ExplorerPermissions.ServiceRead, () =>
    {
        var entries = new List<ServiceEntry>();
        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                entries.Add(new ServiceEntry
                {
                    Name = service.ServiceName,
                    DisplayName = service.DisplayName,
                    Status = Try(() => service.Status.ToString()),
                    StartType = Try(() => service.StartType.ToString()),
                    ServiceType = Try(() => service.ServiceType.ToString()),
                });
            }
        }

        return new ServiceListResponse { Services = entries };
    });

    public ExplorerAckResponse Control(ServiceControlRequest request) => Write(ExplorerPermissions.ServiceControl, $"Sys.Services.{request.Action}", request.Name, () =>
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Service name is empty.");
        var exists = ServiceController.GetServices().Any(s =>
        {
            using (s)
                return string.Equals(s.ServiceName, request.Name, StringComparison.OrdinalIgnoreCase);
        });
        if (!exists)
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Service not found.");

        using var service = new ServiceController(request.Name);
        if (request.Action is ServiceControlAction.Stop or ServiceControlAction.Restart && service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, StatusWait);
        }

        if (request.Action is ServiceControlAction.Start or ServiceControlAction.Restart)
        {
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, StatusWait);
            }
        }
    });

    private static string? Try(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
