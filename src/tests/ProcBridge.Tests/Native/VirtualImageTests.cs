using ProcBridge.Broker.Models;
using ProcBridge.Launcher;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// Running an exe that lives in the virtual folder: the hook starts it from the backing copy (local folder or Broker
/// cache), with the DLLs it imports from next to itself, and the child sees its virtual names again
/// (<c>ImageRedirect</c>, <c>LoaderNames</c> in ProcBridge.Hook).
/// </summary>
[TestClass]
public sealed class VirtualImageTests
{
    private const string BinFolder = "bin";
    private const string ImageName = "app.exe";
    private const string DynamicDll = "pbdyn.dll";

    public TestContext TestContext { get; set; } = null!;

    private static string Lines(ProcessResult result) => result.StandardOutput + result.StandardError;

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void ExeInVirtualFolder_RunsWithItsDllsAndVirtualNames(string platform)
    {
        using HookSandbox sandbox = new(platform);
        CreateImageFolder(sandbox, platform);

        ProcessResult result = sandbox.RunHooked(ImageCommand(sandbox));

        AssertVirtualNames(sandbox, result);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ExeOnMachineB_RunsFromCompleteCacheCopy(string platform)
    {
        using HookSandbox sandbox = new(platform);
        CreateImageFolder(sandbox, platform);
        // Tiny threshold: the image and its DLLs would be ranged copies, which the kernel cannot map without the hook.
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: x => x with { RangeThreshold = 16 * 1024 });

        ProcessResult result = session.Run(ImageCommand(sandbox));

        AssertVirtualNames(sandbox, result);
        string cachedImage = Path.Combine(session.CacheRoot, BinFolder, ImageName);
        Assert.IsTrue(File.Exists(cachedImage), "The process ran from the Broker cache.");
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(sandbox.RealRoot, BinFolder, ImageName)), File.ReadAllBytes(cachedImage),
            "The cache copy is complete.");
    }

    [TestMethod]
    public async Task DotNetAppHost_RunsFromVirtualFolder()
    {
        // The apphost finds ProcBridge.Launcher.dll next to its own (virtual) file name.
        using HookSandbox sandbox = new("x64");
        RemotePurposeTests.CopyFolder(RemotePurposeTests.LauncherOutput(), Path.Combine(sandbox.RealRoot, "app"));
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: x => x with { RangeThreshold = 64 * 1024 });

        ProcessResult result = session.Run($"cmd.exe /d /c \"\"{sandbox.VirtualRoot}\\app\\ProcBridge.Launcher.exe\"\"");

        Assert.AreEqual(2, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardError, LauncherCommandLine.Usage.Split('\n')[0].Trim(), "The apphost ran from machine B.");
    }

    /// <summary>
    /// bin\app.exe (a copy of the probe), a copy of dbghelp.dll next to it (a static import of the probe, which the
    /// loader takes from the image folder first) and pbdyn.dll (a copy of version.dll, loaded by bare name later).
    /// </summary>
    private static void CreateImageFolder(HookSandbox sandbox, string platform)
    {
        string system = platform == "x64" ? Environment.SystemDirectory : Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        string bin = Path.Combine(sandbox.RealRoot, BinFolder);
        Directory.CreateDirectory(bin);
        File.Copy(sandbox.Binaries.Probe, Path.Combine(bin, ImageName));
        File.Copy(Path.Combine(system, "dbghelp.dll"), Path.Combine(bin, "dbghelp.dll"));
        File.Copy(Path.Combine(system, "version.dll"), Path.Combine(bin, DynamicDll));
    }

    // cmd (hooked) creates the process: the creation goes through the hook's NtCreateUserProcess.
    private static string ImageCommand(HookSandbox sandbox)
        => $"cmd.exe /d /c \"\"{sandbox.VirtualRoot}\\{BinFolder}\\{ImageName}\" --image {DynamicDll}\"";

    private static void AssertVirtualNames(HookSandbox sandbox, ProcessResult result)
    {
        string folder = $"{sandbox.VirtualRoot}\\{BinFolder}";
        Assert.AreEqual(0, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardOutput, $"image {folder}\\{ImageName}", "GetModuleFileName reports the virtual image.");
        StringAssert.Contains(result.StandardOutput, $"dbghelp {folder}\\dbghelp.dll", "The static import came from next to the image.");
        StringAssert.Contains(result.StandardOutput, $"dynamic {folder}\\{DynamicDll}", "A DLL loaded later by name is found next to the image.");
    }
}
