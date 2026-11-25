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
