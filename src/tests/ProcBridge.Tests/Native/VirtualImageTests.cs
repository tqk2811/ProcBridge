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

    [TestMethod]
    public async Task ImageMissingOnMachineB_FallsBackToTheOriginalCall()
    {
        // Machine B has no binpp.exe, but a real file sits at the virtual path itself: the hook cannot bring the
        // image in, so the creation must go on unchanged (and find that file) instead of failing.
        using HookSandbox sandbox = new("x64");
        string localBin = Path.Combine(sandbox.VirtualRoot, BinFolder);
        Directory.CreateDirectory(localBin);
        File.Copy(sandbox.Binaries.Probe, Path.Combine(localBin, ImageName));
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --run-image \"{localBin}\\{ImageName}\" \"app --image\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardOutput, $"image {localBin}\\{ImageName}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MalformedImages_FailLikeLocally(bool remote)
    {
        using HookSandbox sandbox = new("x64");
        string bin = Path.Combine(sandbox.RealRoot, BinFolder);
        Directory.CreateDirectory(bin);
        byte[] probe = File.ReadAllBytes(sandbox.Binaries.Probe);
        File.WriteAllBytes(Path.Combine(bin, "garbage.exe"), [(byte)'M', (byte)'Z', .. new byte[200]]);
        File.WriteAllBytes(Path.Combine(bin, "truncated.exe"), probe[..1024]);
        await using RemoteSession? session = remote ? RemoteSession.Start(sandbox) : null;

        foreach (string name in new[] { "garbage.exe", "truncated.exe" })
        {
            string command = $"\"{sandbox.Binaries.Probe}\" --run \"{sandbox.VirtualRoot}\\{BinFolder}\\{name}\"";
            ProcessResult result = session is null ? sandbox.RunHooked(command) : session.Run(command);

            Assert.AreEqual(1, result.ExitCode, Lines(result));
            StringAssert.Contains(result.StandardOutput, "run: ", $"{name}: CreateProcess fails, the creator does not crash.");
        }
    }

    [TestMethod]
    public async Task SameImage_StartedInParallel_RunsEveryTime()
    {
        using HookSandbox sandbox = new("x64");
        CreateImageFolder(sandbox, "x64");
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: x => x with { RangeThreshold = 16 * 1024 });

        ProcessResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => session.Run(ImageCommand(sandbox)))));

        foreach (ProcessResult result in results)
            AssertVirtualNames(sandbox, result);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchFileInVirtualFolder_RunsThroughCmd(bool remote)
    {
        using HookSandbox sandbox = new("x64");
        Directory.CreateDirectory(Path.Combine(sandbox.RealRoot, BinFolder));
        File.WriteAllText(Path.Combine(sandbox.RealRoot, BinFolder, "hello.bat"), "@echo bat-ok %~nx0\r\n");
        await using RemoteSession? session = remote ? RemoteSession.Start(sandbox) : null;
        string command = $"\"{sandbox.Binaries.Probe}\" --run \"{sandbox.VirtualRoot}\\{BinFolder}\\hello.bat\"";

        ProcessResult result = session is null ? sandbox.RunHooked(command) : session.Run(command);

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardOutput, "bat-ok hello.bat");
    }

    [TestMethod]
    public void ChildOfVirtualImage_DoesNotInheritTheImageVariable()
    {
        using HookSandbox sandbox = new("x64");
        CreateImageFolder(sandbox, "x64");

        ProcessResult result = sandbox.RunHooked(
            $"cmd.exe /d /c \"\"{sandbox.VirtualRoot}\\{BinFolder}\\{ImageName}\" --run \"cmd.exe /d /c set PROCBRIDGE_\"\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardOutput, "PROCBRIDGE_MAP=", "The grandchild listed the variables.");
        Assert.DoesNotContain("PROCBRIDGE_IMAGE", result.StandardOutput);
    }

    [TestMethod]
    public async Task ImageChangedOnMachineB_WhileAnInstanceRuns_NewRunUsesTheNewCopy()
    {
        using HookSandbox sandbox = new("x64");
        CreateImageFolder(sandbox, "x64");
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: x => x with { MetadataTtl = TimeSpan.Zero });
        string started = Path.Combine(sandbox.BaseDirectory, "started");
        string go = Path.Combine(sandbox.BaseDirectory, "go");
        string virtualImage = $"{sandbox.VirtualRoot}\\{BinFolder}\\{ImageName}";
        Task<ProcessResult> first = Task.Run(() => session.Run($"cmd.exe /d /c \"\"{virtualImage}\" --image-wait \"{started}\" \"{go}\"\""));
        try
        {
            for (int i = 0; i < 600 && !File.Exists(started) && !first.IsCompleted; i++)
                await Task.Delay(50);
            Assert.IsTrue(File.Exists(started), "The first instance runs from the cache copy.");
            // A new version on machine B (an overlay byte changes size and content) while the old copy is mapped as an image.
            string sourceImage = Path.Combine(sandbox.RealRoot, BinFolder, ImageName);
            byte[] updated = [.. File.ReadAllBytes(sourceImage), 0x5A];
            File.WriteAllBytes(sourceImage, updated);

            ProcessResult second = session.Run(ImageCommand(sandbox));

            AssertVirtualNames(sandbox, second);
            CollectionAssert.AreEqual(updated, File.ReadAllBytes(Path.Combine(session.CacheRoot, BinFolder, ImageName)),
                "The cache holds the new version.");
        }
        finally
        {
            File.WriteAllText(go, "");
        }
        ProcessResult firstResult = await first;
        Assert.AreEqual(0, firstResult.ExitCode, Lines(firstResult));
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
