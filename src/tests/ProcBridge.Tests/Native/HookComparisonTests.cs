using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// Differential test (plan section 12): NativeProbe on the real folder and NativeProbe through the
/// virtual folder must print the same lines (paths are printed relative to the given root).
/// </summary>
[TestClass]
public sealed class HookComparisonTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void Probe_ThroughVirtualFolder_MatchesRealFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"\"{sandbox.RealRoot}\"");
        ProcessResult hooked = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" \"{sandbox.VirtualRoot}\"");

        Assert.AreEqual(0, real.ExitCode, real.StandardError);
        Assert.AreEqual(0, hooked.ExitCode, hooked.StandardError);
        Assert.Contains("ReadFile: <ROOT>\\a.txt -> size=", real.StandardOutput);
        AssertSameLines(real.StandardOutput, hooked.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void Probe_VirtualFolderWithoutHook_DiffersFromRealFolder(string platform)
    {
        // Control: proves the comparison can fail, i.e. the equality above comes from the hook.
        using HookSandbox sandbox = new(platform);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"\"{sandbox.RealRoot}\"");
        ProcessResult unhooked = sandbox.Run(sandbox.Binaries.Probe, $"\"{sandbox.VirtualRoot}\"");

        Assert.AreNotEqual(real.StandardOutput, unhooked.StandardOutput);
        Assert.Contains("ReadFile: <ROOT>\\a.txt -> err=3", unhooked.StandardOutput);
    }

    internal static void AssertSameLines(string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        for (int i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
        {
            Assert.AreEqual(expectedLines[i], actualLines[i], $"First difference at line {i + 1}.");
        }
        Assert.HasCount(expectedLines.Length, actualLines);
    }
}
