using System.Runtime.InteropServices;
using System.Text;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// Spellings of a virtual path other than \??\ with long names (plan 4.3): \DosDevices\ and \GLOBAL??\ prefixes,
/// 8.3 short names in the path or in the declared root, and opens by file id.
/// </summary>
[TestClass]
public sealed class PathSpellingTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void DosDevicesAndGlobalPrefixes_OpenVirtualFile(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";

        foreach (string prefix in new[] { "\\DosDevices\\", "\\GLOBAL??\\" })
        {
            ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --nt-final-name \"{prefix}{file}\"");

            Assert.AreEqual(0, result.ExitCode, prefix + ": " + result.StandardOutput + result.StandardError);
            Assert.AreEqual($"name: \\\\?\\{file}", result.StandardOutput.TrimEnd(), prefix);
        }
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void ShortNames_InPathOrInRoot_OpenVirtualFile(string platform)
    {
        // A long folder name with spaces under %TEMP% (8.3 names are usually on for the system volume).
        string baseDirectory = Path.Combine(Path.GetTempPath(), "ProcBridge long sandbox " + Guid.NewGuid().ToString("N"));
        using HookSandbox sandbox = new(platform, baseDirectory);
        string longBase = ConvertPath(baseDirectory, shortForm: false);
        string shortBase = ConvertPath(baseDirectory, shortForm: true);
        if (string.Equals(longBase, shortBase, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive($"8.3 names are disabled on the volume of {baseDirectory}.");
        string leaf = Path.GetFileName(sandbox.VirtualRoot);
        string expected = $"name: \\\\?\\{longBase}\\{leaf}\\a.txt";

        ProcessResult shortPath = sandbox.RunSpawn(
            $"--map \"{longBase}\\{leaf}\" \"{sandbox.RealRoot}\" -- \"{sandbox.Binaries.Probe}\" --final-name \"{shortBase}\\{leaf}\\a.txt\"");
        ProcessResult shortRoot = sandbox.RunSpawn(
            $"--map \"{shortBase}\\{leaf}\" \"{sandbox.RealRoot}\" -- \"{sandbox.Binaries.Probe}\" --final-name \"{longBase}\\{leaf}\\a.txt\"");

        Assert.AreEqual(0, shortPath.ExitCode, shortPath.StandardOutput + shortPath.StandardError);
        Assert.AreEqual(expected, shortPath.StandardOutput.TrimEnd(), "path spelled with an 8.3 name");
        Assert.AreEqual(0, shortRoot.ExitCode, shortRoot.StandardOutput + shortRoot.StandardError);
        Assert.AreEqual(expected, shortRoot.StandardOutput.TrimEnd(), "root declared with an 8.3 name");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void OpenByFileId_LocalMapping_FollowsRootHandle(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult virtualRoot = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --open-by-id \"{sandbox.VirtualRoot}\" \"{file}\"");
        ProcessResult realRoot = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --open-by-id \"{sandbox.RealRoot}\" \"{file}\"");

        Assert.AreEqual(0, virtualRoot.ExitCode, virtualRoot.StandardOutput + virtualRoot.StandardError);
        Assert.AreEqual($"by-id: \\\\?\\{file}", virtualRoot.StandardOutput.TrimEnd());
        Assert.AreEqual(0, realRoot.ExitCode, realRoot.StandardOutput + realRoot.StandardError);
        Assert.AreEqual($"by-id: \\\\?\\{sandbox.RealRoot}\\a.txt", realRoot.StandardOutput.TrimEnd());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task OpenByFileId_ThroughAgent_IsNotSupported(string platform)
    {
        // The process only sees machine B's file ids; on the cache volume they would open an unrelated file.
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --open-by-id \"{sandbox.VirtualRoot}\" \"{file}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        // ERROR_NOT_SUPPORTED.
        Assert.AreEqual("by-id: err=50", result.StandardOutput.TrimEnd());
    }

    private static string ConvertPath(string path, bool shortForm)
    {
        StringBuilder buffer = new(32768);
        uint length = shortForm ? GetShortPathNameW(path, buffer, (uint)buffer.Capacity) : GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
            throw new InvalidOperationException($"Cannot convert {path}: error {Marshal.GetLastWin32Error()}.");
        return buffer.ToString(0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}
