using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>ProcBridge.Spawn argument handling and refusals.</summary>
[TestClass]
public sealed class SpawnTests
{
    private const int SpawnFailure = 125;

    [TestMethod]
    [DataRow("x64", "virtual")]
    [DataRow("x64", "real")]
    [DataRow("Win32", "virtual")]
    public void DriveRootMapping_IsRefused(string platform, string side)
    {
        using HookSandbox sandbox = new(platform);
        // "D:\\" quoted: the doubled backslash keeps the closing quote a quote.
        string root = Path.GetPathRoot(sandbox.RealRoot) + "\\";
        string map = side == "virtual" ? $"\"{root}\" \"{sandbox.RealRoot}\"" : $"\"{sandbox.VirtualRoot}\" \"{root}\"";

        ProcessResult result = sandbox.RunSpawn($"--map {map} -- cmd.exe /d /c echo started");

        Assert.AreEqual(SpawnFailure, result.ExitCode, result.StandardOutput);
        Assert.Contains("--map folders must be on a drive letter", result.StandardError);
        Assert.DoesNotContain("started", result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void SeparatorInsideQuotedMapPath_IsNotTheSeparator(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string virtualRoot = Path.Combine(sandbox.BaseDirectory, "v -- x");

        ProcessResult result = sandbox.RunSpawn(
            $"--map \"{virtualRoot}\" \"{sandbox.RealRoot}\" -- cmd.exe /d /c type \"{virtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64", "SysWOW64")]
    [DataRow("Win32", "Sysnative")]
    public void TargetOfOtherArchitecture_IsRefusedClearly(string platform, string systemFolder)
    {
        // A 32-bit Spawn sees the 64-bit System32 only through Sysnative (WOW64 redirection).
        using HookSandbox sandbox = new(platform);
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), systemFolder, "cmd.exe");

        ProcessResult result = sandbox.RunHooked($"\"{cmd}\" /d /c echo started");

        Assert.AreEqual(SpawnFailure, result.ExitCode, result.StandardOutput + result.StandardError);
        // ERROR_EXE_MACHINE_TYPE_MISMATCH.
        Assert.Contains("target architecture differs", result.StandardError);
        Assert.Contains("(error 216)", result.StandardError);
        Assert.DoesNotContain("started", result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void UncMapping_IsRefused(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunSpawn($"--map \"\\\\localhost\\c$\\virtual\" \"{sandbox.RealRoot}\" -- cmd.exe /d /c echo started");

        Assert.AreEqual(SpawnFailure, result.ExitCode, result.StandardOutput);
        Assert.Contains("--map folders must be on a drive letter", result.StandardError);
    }
}
