using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

/// <summary>Never starts or stops a real service: control is only exercised on denied or unknown names.</summary>
[TestClass]
public sealed class ServiceAndSystemInfoTests
{
    private static ExplorerSession Session(ExplorerPermissions permissions, RecordingAuditLog? audit = null)
        => new(new ExplorerPolicy { Permissions = permissions }, "machine-a", audit);

    [TestMethod]
    public void ServiceList_WithServiceRead_ContainsEventLog()
    {
        var response = Session(ExplorerPermissions.ServiceRead).Services.List(new ServiceListRequest());

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        var eventLog = response.Services.Single(s => string.Equals(s.Name, "EventLog", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("Running", eventLog.Status);
    }

    [TestMethod]
    public void ServiceList_WithoutServiceRead_IsPermissionDenied()
    {
        Assert.AreEqual(ExplorerStatus.PermissionDenied, Session(ExplorerPermissions.ServiceControl).Services.List(new ServiceListRequest()).Status);
    }

    [TestMethod]
    public void ServiceControl_WithoutPermission_IsDeniedAndAudited()
    {
        var audit = new RecordingAuditLog();

        var response = Session(ExplorerPermissions.ServiceRead, audit).Services.Control(new ServiceControlRequest { Name = "EventLog", Action = ServiceControlAction.Stop });

        Assert.AreEqual(ExplorerStatus.PermissionDenied, response.Status);
        Assert.AreEqual("Sys.Services.Stop", audit.Entries.Single().Action);
    }

    [TestMethod]
    public void ServiceControl_UnknownService_IsNotFound()
    {
        var response = Session(ExplorerPermissions.ServiceControl).Services.Control(new ServiceControlRequest { Name = "ProcBridgeNoSuchService_" + Guid.NewGuid().ToString("N"), Action = ServiceControlAction.Start });

        Assert.AreEqual(ExplorerStatus.NotFound, response.Status);
    }

    [TestMethod]
    public void SystemInfo_WithPermission_ReturnsMachine()
    {
        var response = Session(ExplorerPermissions.SystemInfo).SystemInfo.Get(new SystemInfoRequest());

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual(Environment.MachineName, response.MachineName);
        Assert.AreEqual(Environment.ProcessorCount, response.ProcessorCount);
        Assert.IsTrue(response.TotalMemoryBytes > 0);
        Assert.IsFalse(string.IsNullOrEmpty(response.AgentUser));
    }

    [TestMethod]
    public void SystemInfo_WithoutPermission_IsPermissionDenied()
    {
        Assert.AreEqual(ExplorerStatus.PermissionDenied, Session(ExplorerPermissions.None).SystemInfo.Get(new SystemInfoRequest()).Status);
    }

    [TestMethod]
    public void Session_RejectsBadRoots()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ExplorerSession(new ExplorerPolicy { FileRoots = [@"\\server\share"] }, "machine-a"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExplorerSession(new ExplorerPolicy { RegistryRoots = [@"BAD\Key"] }, "machine-a"));
    }
}
