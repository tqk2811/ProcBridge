using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// Review fixes of M4 (plan 5, 13.2): which virtual handles a child receives, how the hook sees the first
/// resume of a suspended child, and what the cross-architecture helper leaks. NativeProbe runs hooked
/// and creates the children itself (--child modes).
/// </summary>
[TestClass]
public sealed class ChildCreationTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void SuspendedChild_HandleClosedBeforeResume_KeepsVirtualName(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --child suspended-close \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual($"handle: \\\\?\\{sandbox.VirtualRoot}\\a.txt", result.StandardOutput.TrimEnd());
    }

    [TestMethod]
    [DataRow("x64", "handle-list")]
    [DataRow("Win32", "handle-list")]
    [DataRow("x64", "parent-process")]
    [DataRow("Win32", "parent-process")]
    public void ChildNotInheritingVirtualHandle_DoesNotReceiveItsEntry(string platform, string mode)
    {
        // Exit code of the child: 0 = the value holding the real file shows the real name, 1 = it shows
        // the virtual name of an entry the child should not have received. The real file lies outside the backing
        // folder: a handle to a file under it is what a virtual handle is, and the child's lazy check would adopt it.
        using HookSandbox sandbox = new(platform);
        string outside = Path.Combine(sandbox.BaseDirectory, "outside.txt");
        File.WriteAllText(outside, "outside");

        ProcessResult result = sandbox.RunHooked(
            $"\"{sandbox.Binaries.Probe}\" --child {mode} \"{sandbox.VirtualRoot}\\a.txt\" \"{outside}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("child: 0", result.StandardOutput.TrimEnd());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void SuspendedChild_ResumedWithNtAlertResumeThread_IsHooked(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --child alert-resume \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual($"name: \\\\?\\{sandbox.VirtualRoot}\\a.txt", result.StandardOutput.TrimEnd());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void SuspendedChild_ResumedFromTwoThreads_RunsOnlyAfterInjection(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string expected = $"name: \\\\?\\{sandbox.VirtualRoot}\\a.txt";

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --child double-resume \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.HasCount(5, lines, result.StandardOutput);
        foreach (string line in lines)
        {
            Assert.AreEqual(expected, line, result.StandardOutput);
        }
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void SuspendedChildTerminatedBeforeResume_ReleasesPendingEntry(string platform)
    {
        // 20 children terminated before any resume: their pending entries (one process handle each) must
        // go away without waiting for the next child creation.
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --child pending-exit");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string growth = result.StandardOutput.TrimEnd();
        Assert.StartsWith("growth: ", growth);
        Assert.IsLessThan(10, int.Parse(growth["growth: ".Length..]), growth);
    }

    [TestMethod]
    [DataRow("x64", "Win32")]
    [DataRow("Win32", "x64")]
    public void CrossArchitectureInjection_DoesNotLeakHandlesToSiblings(string platform, string otherPlatform)
    {
        using HookSandbox sandbox = new(platform);
        NativeBinaries other = new(otherPlatform);

        ProcessResult result = sandbox.RunHooked(
            $"\"{sandbox.Binaries.Probe}\" --child sibling-leak \"{other.Probe}\" \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("leaked: 0", result.StandardOutput.TrimEnd(), result.StandardError);
    }
}
