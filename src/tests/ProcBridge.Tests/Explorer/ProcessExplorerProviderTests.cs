using System.Diagnostics;
using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

/// <summary>Only processes started by the test itself are killed.</summary>
[TestClass]
public sealed class ProcessExplorerProviderTests
{
    private Process _child = null!;

    [TestInitialize]
    public void Setup()
    {
        _child = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!_child.HasExited)
            _child.Kill();
        _child.Dispose();
    }

    private static ExplorerSession Session(ExplorerPermissions permissions, RecordingAuditLog? audit = null)
        => new(new ExplorerPolicy { Permissions = permissions }, "machine-a", audit);

    [TestMethod]
    public void List_ContainsChildWithParentAndPath()
    {
        var response = Session(ExplorerPermissions.ProcessRead).Processes.List(new ProcessListRequest());

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        var entry = response.Processes.Single(p => p.Pid == _child.Id);
        Assert.AreEqual(Environment.ProcessId, entry.ParentPid);
        Assert.AreEqual("PING", entry.Name, ignoreCase: true);
        Assert.IsTrue(entry.ImagePath!.EndsWith(@"\ping.exe", StringComparison.OrdinalIgnoreCase), entry.ImagePath);
        Assert.IsNotNull(entry.StartTimeUtc);
    }

    [TestMethod]
    public void List_WithoutProcessRead_IsPermissionDenied()
    {
        Assert.AreEqual(ExplorerStatus.PermissionDenied, Session(ExplorerPermissions.ProcessKill).Processes.List(new ProcessListRequest()).Status);
    }

    [TestMethod]
    public void Kill_WithoutProcessKill_IsDeniedAndChildSurvives()
    {
        var audit = new RecordingAuditLog();

        var response = Session(ExplorerPermissions.ProcessRead, audit).Processes.Kill(new ProcessKillRequest { Pid = _child.Id });

        Assert.AreEqual(ExplorerStatus.PermissionDenied, response.Status);
        Assert.IsFalse(_child.HasExited);
        Assert.AreEqual("Proc.Kill", audit.Entries.Single().Action);
    }

    [TestMethod]
    public void Kill_WithProcessKill_EndsChild()
    {
        var response = Session(ExplorerPermissions.ProcessKill).Processes.Kill(new ProcessKillRequest { Pid = _child.Id });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsTrue(_child.WaitForExit(5000));
    }

    [TestMethod]
    public void Kill_SelfOrSystem_IsAccessDenied()
    {
        var processes = Session(ExplorerPermissions.ProcessKill).Processes;

        Assert.AreEqual(ExplorerStatus.AccessDenied, processes.Kill(new ProcessKillRequest { Pid = Environment.ProcessId }).Status);
        Assert.AreEqual(ExplorerStatus.AccessDenied, processes.Kill(new ProcessKillRequest { Pid = 4 }).Status);
    }

    [TestMethod]
    public void Kill_UnknownPid_IsNotFound()
    {
        // PIDs are multiples of 4, so an odd one never exists.
        Assert.AreEqual(ExplorerStatus.NotFound, Session(ExplorerPermissions.ProcessKill).Processes.Kill(new ProcessKillRequest { Pid = 0x7FFFFFF1 }).Status);
    }
}
