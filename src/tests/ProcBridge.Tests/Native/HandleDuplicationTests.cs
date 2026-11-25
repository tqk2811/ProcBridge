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
    [DataRow("x64")]
    [DataRow("Win32")]
    public void DuplicateIntoChild_LocalMapping_KeepsVirtualName(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\"");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task DuplicateIntoChild_ThroughAgent_KeepsMachineBView(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\"");

        AssertChildSeesParentView(file, result);
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
