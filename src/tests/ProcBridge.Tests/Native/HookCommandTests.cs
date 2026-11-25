using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M1 acceptance: everyday tools read through the virtual folder. A 32-bit Spawn starts the 32-bit
/// cmd/powershell automatically (WOW64 file system redirection of System32).
/// </summary>
[TestClass]
public sealed class HookCommandTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void CmdType_ReadsFileThroughVirtualFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void CmdDir_ListsVirtualFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"cmd.exe /d /c dir \"{sandbox.VirtualRoot}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.Contains(sandbox.VirtualRoot, result.StandardOutput);
        Assert.Contains("a.txt", result.StandardOutput);
        Assert.IsTrue(result.StandardOutput.Split(Environment.NewLine).Any(line => line.EndsWith(" sub", StringComparison.Ordinal)),
            result.StandardOutput);
        Assert.Contains("ro.txt", result.StandardOutput);

        // Bare listing must match the real folder entry for entry.
        ProcessResult real = sandbox.Run("cmd.exe", $"/d /c dir /b /a \"{sandbox.RealRoot}\"");
        ProcessResult hooked = sandbox.RunHooked($"cmd.exe /d /c dir /b /a \"{sandbox.VirtualRoot}\"");
        Assert.AreEqual(0, hooked.ExitCode, hooked.StandardError);
        Assert.AreEqual(real.StandardOutput, hooked.StandardOutput);
        Assert.Contains("a.txt", hooked.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void CmdCd_WorksInsideVirtualFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked(
            $"cmd.exe /d /c \"cd /d \"{sandbox.VirtualRoot}\\sub\" && cd && type b.txt && dir /b deep\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string[] lines = result.StandardOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[] { sandbox.VirtualRoot + "\\sub", "bee", "c.bin" }, lines);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void DotNetFramework_ReadAllText_ReadsThroughVirtualFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string script = $"[IO.File]::ReadAllText('{sandbox.VirtualRoot}\\a.txt');"
            + $"[IO.Directory]::GetFiles('{sandbox.VirtualRoot}\\sub\\deep');"
            + $"(Get-Item '{sandbox.VirtualRoot}\\sub\\b.txt').Length";

        ProcessResult result = sandbox.RunHooked($"powershell.exe -NoProfile -NonInteractive -Command \"{script}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string[] lines = result.StandardOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[] { "hello from the real folder", sandbox.VirtualRoot + "\\sub\\deep\\c.bin", "5" }, lines);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void PathOutsideVirtualFolder_IsNotRedirected(string platform)
    {
        using HookSandbox sandbox = new(platform);
        // "realx\a.txt" is where "virtual-rootx\a.txt" would land if the prefix match ignored the path boundary.
        const string marker = "sibling of the real root";
        string realSibling = sandbox.RealRoot + "x";
        Directory.CreateDirectory(realSibling);
        File.WriteAllText(Path.Combine(realSibling, "a.txt"), marker);

        ProcessResult result = sandbox.RunHooked($"cmd.exe /d /c type \"{sandbox.RealRoot}\\sub\\b.txt\" \"{sandbox.VirtualRoot}x\\a.txt\"");

        // Real path still works; a sibling sharing the prefix ("virtual-rootx") is not inside the virtual root.
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("bee", result.StandardOutput);
        Assert.DoesNotContain(marker, result.StandardOutput);
    }
}
