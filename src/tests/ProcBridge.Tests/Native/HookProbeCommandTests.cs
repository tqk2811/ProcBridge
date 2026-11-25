using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// Single checks run by NativeProbe commands (--name ...) under the hook: cases a differential run
/// cannot express (handle reuse, deleting, threads).
/// </summary>
[TestClass]
public sealed class HookProbeCommandTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void StaleHandle_ReusedForRealFile_ReportsRealPath(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string realFile = Path.Combine(sandbox.RealRoot, "a.txt");

        ProcessResult result = sandbox.RunHooked(
            $"\"{sandbox.Binaries.Probe}\" --stale-handle \"{sandbox.VirtualRoot}\\a.txt\" \"{realFile}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        // A handle closed without NtClose must not keep its virtual name for the next file with that value.
        Assert.AreEqual($"reused: \\\\?\\{realFile}", result.StandardOutput.TrimEnd());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void NtDeleteFile_DeletesBackingFile(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string realFile = Path.Combine(sandbox.RealRoot, "sub", "delete-me.txt");
        File.WriteAllText(realFile, "x");

        ProcessResult result = sandbox.RunHooked(
            $"\"{sandbox.Binaries.Probe}\" --delete \"{sandbox.VirtualRoot}\\sub\\delete-me.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("delete: status=0x0", result.StandardOutput.TrimEnd());
        Assert.IsFalse(File.Exists(realFile));
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void FailedClose_KeepsVirtualName(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string virtualFile = Path.Combine(sandbox.VirtualRoot, "a.txt");

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --failed-close \"{virtualFile}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        // ERROR_INVALID_HANDLE: CloseHandle on a handle protected from close.
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[] { "close: err=6", $"after: \\\\?\\{virtualFile}" }, lines);
    }
}
