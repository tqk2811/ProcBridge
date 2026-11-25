using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// A virtual root does not exist in its real parent folder: listings of the parent (FindFirstFile without wildcard,
/// "dir", .NET-style scans without pattern, GetLongPathName) must still show it, once, as a directory.
/// </summary>
[TestClass]
public sealed class ParentListingTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void VirtualRoot_ListedInParentFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --parent-listing \"{sandbox.VirtualRoot}\"");

        AssertListed(sandbox, result);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task VirtualRoot_ListedInParentFolder_ThroughAgent(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --parent-listing \"{sandbox.VirtualRoot}\"");

        AssertListed(sandbox, result);
    }

    [TestMethod]
    [DataRow("x64")]
    public void CmdDir_ShowsVirtualRootInParent(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"cmd.exe /d /c dir /b /ad \"{sandbox.BaseDirectory}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string[] lines = result.StandardOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEquivalent(new[] { "real", Path.GetFileName(sandbox.VirtualRoot) }, lines);
    }

    private static void AssertListed(HookSandbox sandbox, ProcessResult result)
    {
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string leaf = Path.GetFileName(sandbox.VirtualRoot);
        // 0x80000006: STATUS_NO_MORE_FILES once the scan (real entries, then the virtual root) is over.
        string[] expected =
        [
            $"find-exact: {leaf} dir=1",
            $"find-upper: {leaf} dir=1",
            $"find-wild: {leaf} dir=1",
            "nt-overlapped: count=0 status=0x80000006",
            "find-star: count=1 dir=1",
            "nt-null: count=1 status=0x80000006",
            "nt-single: count=1 status=0x80000006",
            "nt-restart: count=2 status=0x80000006",
            $"long: {sandbox.VirtualRoot}\\sub",
        ];
        CollectionAssert.AreEqual(expected, result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries), result.StandardOutput);
    }
}
