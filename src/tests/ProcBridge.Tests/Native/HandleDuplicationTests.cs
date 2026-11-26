using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// A virtual handle duplicated into another hooked process (DuplicateHandle with the child as target, the value passed
/// over a pipe) is tracked there too: the child sees the virtual name and, through the Broker, machine B's identity.
/// </summary>
[TestClass]
public sealed class HandleDuplicationTests
{
    [TestMethod]
    [DataRow("x64", "")]
    [DataRow("Win32", "")]
    [DataRow("x64", "close-source")]
    [DataRow("Win32", "close-source")]
    public void DuplicateIntoChild_LocalMapping_KeepsVirtualName(string platform, string mode)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{sandbox.Binaries.Probe}\" {mode}");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64", "")]
    [DataRow("Win32", "")]
    [DataRow("x64", "close-source")]
    [DataRow("Win32", "close-source")]
    public async Task DuplicateIntoChild_ThroughAgent_KeepsMachineBView(string platform, string mode)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{sandbox.Binaries.Probe}\" {mode}");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64", "write-close-source")]
    [DataRow("Win32", "write-close-source")]
    [DataRow("x64", "write")]
    [DataRow("Win32", "write")]
    [Ignore("Red until the write token follows the duplicated handle (HandleSharing-Design-vi.md section 9 step 5).")]
    public async Task ChildWritingDuplicatedHandleAfterParentClosed_LandsOnMachineB(string platform, string mode)
    {
        // The parent creates the file, duplicates its write handle into a child and closes its own (with
        // DUPLICATE_CLOSE_SOURCE or right after), then the child appends three times. Write-back must wait for the
        // child: everything it wrote reaches machine B, not only what the parent wrote.
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{sandbox.VirtualRoot}\\out.txt\" \"\" {mode}");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        // Checked before the session ends: the child's own close wrote it back, not the final sync of open files.
        Assert.AreEqual("parent+a+b+c", File.ReadAllText(Path.Combine(sandbox.RealRoot, "out.txt")));
        Assert.AreEqual(0, session.Broker.WriteBack.PendingCount);
    }

    [TestMethod]
    [DataRow("x64", "Win32")]
    [DataRow("Win32", "x64")]
    public void DuplicateIntoChildOfOtherArchitecture_KeepsVirtualName(string platform, string childPlatform)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";
        string childProbe = new NativeBinaries(childPlatform).Probe;

        ProcessResult local = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{childProbe}\"");

        AssertChildSeesParentView(file, local);
    }

    [TestMethod]
    [DataRow("x64", "Win32")]
    [DataRow("Win32", "x64")]
    public async Task DuplicateIntoChildOfOtherArchitecture_ThroughAgent_KeepsMachineBView(string platform, string childPlatform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string file = sandbox.VirtualRoot + "\\a.txt";
        string childProbe = new NativeBinaries(childPlatform).Probe;

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{childProbe}\"");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void SquattedInbox_IsNotUsed(string platform)
    {
        // Another process took the child's inbox name first (with a DACL that lets anyone write): the child's hook must
        // not use it, so the duplicated handle stays untracked there and shows its real name.
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"\" squat");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines, result.StandardOutput);
        Assert.StartsWith($"child: \\\\?\\{sandbox.RealRoot}\\a.txt id ", lines[1]);
    }

    private static void AssertChildSeesParentView(string file, ProcessResult result)
    {
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines, result.StandardOutput);
        Assert.StartsWith($"parent: \\\\?\\{file} id ", lines[0]);
        // Same name, file id and write time as the parent's handle (with the Broker: machine B's, not the cache copy's).
        Assert.AreEqual("child:" + lines[0]["parent:".Length..], lines[1]);
    }
}
