using System.Diagnostics;
using ProcBridge.Broker.Models;
using ProcBridge.Launcher;
using ProcBridge.Tests.Lan;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M8 acceptance (plan sections 11 and 12): games read large files by ranges, mappings and image loads of ranged files,
/// a .NET app and a native DLL living on machine B, debug symbols read through the virtual folder, byte range locks and
/// share mode carried to machine B, git on a repository of machine B.
/// </summary>
[TestClass]
public sealed class RemotePurposeTests
{
    private const long OneGigabyte = 1L << 30;

    public TestContext TestContext { get; set; } = null!;

    private static string Lines(ProcessResult result) => result.StandardOutput + result.StandardError;

    private static Func<BrokerOptions, BrokerOptions> Threshold(long bytes) => x => x with { RangeThreshold = bytes };

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task GameAsset_RandomReads_DownloadOnlyTheRangesRead(string platform)
    {
        using HookSandbox sandbox = new(platform);
        long[] data = [123_456_789, 700_000_000, OneGigabyte - 600_000];
        RangeCacheTests.CreateSparseFile(Path.Combine(sandbox.RealRoot, "game", "assets.pak"), OneGigabyte, 11, data);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        // 1 MB at a random offset, then smaller reads spread over the file (explicit offsets and the file pointer).
        string ranges = $"{data[0] + 1000} 1048576 {data[1] + 77} 65536 {data[2]} 4096 {OneGigabyte - 10} 10 5000 100";

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"--read-at \"{sandbox.RealRoot}\\game\\assets.pak\" {ranges}");
        ProcessResult remote = session.Run($"\"{sandbox.Binaries.Probe}\" --read-at \"{sandbox.VirtualRoot}\\game\\assets.pak\" {ranges}");

        Assert.AreEqual(0, real.ExitCode, Lines(real));
        Assert.AreEqual(0, remote.ExitCode, Lines(remote));
        Assert.AreEqual(real.StandardOutput, remote.StandardOutput, "Ranges read through the hook must match machine B.");
        long downloaded = session.Broker.Cache.RangeBytesDownloaded;
        TestContext.WriteLine($"{platform}: 5 reads (1 MB + 70 KB) of a 1 GB file downloaded {downloaded} bytes.");
        // 1 MB at an unaligned offset spans 5 blocks of 256 KB, each other read one block.
        Assert.IsLessThanOrEqualTo(9L * 256 * 1024, downloaded, "Only the blocks under the reads come over the network.");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task MappedRangedFile_IsCompletedBeforeTheMapping(string platform)
    {
        using HookSandbox sandbox = new(platform);
        RangeCacheTests.CreateSparseFile(Path.Combine(sandbox.RealRoot, "level.dat"), 3L * 1024 * 1024 + 5, 12, 0, 2L * 1024 * 1024);
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: Threshold(1024 * 1024));
        string virtualFile = $"{sandbox.VirtualRoot}\\level.dat";

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"--map-hash \"{sandbox.RealRoot}\\level.dat\"");
        ProcessResult partly = session.Run($"\"{sandbox.Binaries.Probe}\" --read-at \"{virtualFile}\" 10 100");
        Assert.AreEqual(0, partly.ExitCode, Lines(partly));
        Assert.AreEqual(256L * 1024, session.Broker.Cache.RangeBytesDownloaded, "The read brought one block in.");
        ProcessResult mapped = session.Run($"\"{sandbox.Binaries.Probe}\" --map-hash \"{virtualFile}\"");

        Assert.AreEqual(0, real.ExitCode, Lines(real));
        Assert.AreEqual(0, mapped.ExitCode, Lines(mapped));
        Assert.AreEqual(real.StandardOutput, mapped.StandardOutput, "A mapping sees no zero holes: the copy is completed first.");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task NativeDll_LoadsFromVirtualFolder_AsRangedImage(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string system = platform == "x64" ? Environment.SystemDirectory : Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        Directory.CreateDirectory(Path.Combine(sandbox.RealRoot, "bin"));
        File.Copy(Path.Combine(system, "version.dll"), Path.Combine(sandbox.RealRoot, "bin", "pbver.dll"));
        // Tiny threshold: the DLL is a ranged copy, so the image section must bring it in whole first.
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: Threshold(16 * 1024));

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --load \"{sandbox.VirtualRoot}\\bin\\pbver.dll\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardOutput, "load ok size=nonzero");
        StringAssert.Contains(result.StandardOutput, $"module {sandbox.VirtualRoot}\\bin\\pbver.dll", "The process sees the DLL at its virtual path.");
        Assert.IsGreaterThan(0L, session.Broker.Cache.RangeBytesDownloaded);
    }

    [TestMethod]
    public async Task DotNetApp_RunsFromVirtualFolder()
    {
        using HookSandbox sandbox = new("x64");
        string app = Path.Combine(sandbox.RealRoot, "app");
        CopyFolder(LauncherOutput(), app);
        // Larger assemblies become ranged copies: the runtime maps them (image sections) after reading their headers.
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: Threshold(64 * 1024));

        ProcessResult result = session.Run($"dotnet.exe \"{sandbox.VirtualRoot}\\app\\ProcBridge.Launcher.dll\"");

        Assert.AreEqual(2, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardError, LauncherCommandLine.Usage.Split('\n')[0].Trim(), "The app ran from machine B and printed its usage.");
        TestContext.WriteLine($"Ranged bytes downloaded while starting the app: {session.Broker.Cache.RangeBytesDownloaded}.");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task DbgHelp_ReadsPdbFromVirtualFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string folder = Path.Combine(sandbox.RealRoot, "dbg");
        Directory.CreateDirectory(folder);
        string probe = sandbox.Binaries.Probe;
        File.Copy(probe, Path.Combine(folder, Path.GetFileName(probe)));
        File.Copy(Path.ChangeExtension(probe, ".pdb"), Path.Combine(folder, Path.GetFileName(Path.ChangeExtension(probe, ".pdb"))));
        await using RemoteSession session = RemoteSession.Start(sandbox, configure: Threshold(256 * 1024));
        string virtualExe = $"{sandbox.VirtualRoot}\\dbg\\{Path.GetFileName(probe)}";

        ProcessResult result = session.Run($"\"{probe}\" --symbols \"{virtualExe}\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        StringAssert.Contains(result.StandardOutput, $"symbols type=3 pdb={sandbox.VirtualRoot}\\dbg\\", "DbgHelp read the pdb (SymPdb) through the virtual folder.");
        StringAssert.Contains(result.StandardOutput, "wmain found");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ByteRangeLocksAndShareMode_ReachMachineB(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string real = Path.Combine(sandbox.RealRoot, "locked.db");
        File.WriteAllBytes(real, new byte[1000]);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --lock \"{sandbox.VirtualRoot}\\locked.db\" \"{real}\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        string expected = string.Join("\n",
            "real write open: err=32", // the writer on machine A shares reading only
            "real read open: ok",
            "lock: ok",
            "real lock: err=33", // machine B sees the lock of machine A
            "unlock: ok",
            "real lock after unlock: ok",
            "lock while machine B holds: err=33", // machine A sees the lock of machine B
            "lock again: ok",
            "write: ok",
            "flush: ok",
            "real after flush: LOCKED", // written back in place, through the held handle
            "real write open after close: ok") + "\n";
        Assert.AreEqual(expected, result.StandardOutput.Replace("\r\n", "\n"), Lines(result));
        Assert.AreEqual(0, session.Broker.Holds.Count, "Closing the file lets machine B go.");
        Assert.AreEqual("LOCKED", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(real), 0, 6));
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task OverlappedLockOnACompletionPort_CompletesThroughThePort(string platform)
    {
        using HookSandbox sandbox = new(platform);
        File.WriteAllBytes(Path.Combine(sandbox.RealRoot, "async.db"), new byte[1000]);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --lock-async \"{sandbox.VirtualRoot}\\async.db\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        string expected = string.Join("\n",
            "lock: ok",
            "packet: key=7", // the lock granted on machine B is reported on the port, like a local one
            "lock skipping the port: ok",
            "packet: err=258") + "\n"; // FILE_SKIP_COMPLETION_PORT_ON_SUCCESS: no packet (WAIT_TIMEOUT)
        Assert.AreEqual(expected, result.StandardOutput.Replace("\r\n", "\n"), Lines(result));
    }

    [TestMethod]
    public async Task Git_StatusAndLog_OnRepositoryOfMachineB()
    {
        string? git = FindGit();
        if (git is null)
            Assert.Inconclusive("git is not installed.");
        using HookSandbox sandbox = new("x64");
        string repo = Path.Combine(sandbox.RealRoot, "repo");
        Directory.CreateDirectory(repo);
        RunGit(git, repo, "init -q");
        File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\n");
        RunGit(git, repo, "add readme.txt");
        RunGit(git, repo, "-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit -q -m \"first commit on B\"");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "x");
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);
        string virtualRepo = $"{sandbox.VirtualRoot}\\repo";

        ProcessResult status = session.Run($"\"{git}\" -c safe.directory=* -C \"{virtualRepo}\" status --porcelain");
        ProcessResult log = session.Run($"\"{git}\" -c safe.directory=* -C \"{virtualRepo}\" log --oneline");

        Assert.AreEqual(0, status.ExitCode, Lines(status));
        Assert.AreEqual("?? new.txt\n", status.StandardOutput.Replace("\r\n", "\n"), Lines(status));
        Assert.AreEqual(0, log.ExitCode, Lines(log));
        StringAssert.Contains(log.StandardOutput, "first commit on B");
    }

    private static string? FindGit()
    {
        foreach (string candidate in new[] { @"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files\Git\bin\git.exe" })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static void RunGit(string git, string directory, string arguments)
    {
        var start = new ProcessStartInfo(git, arguments) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        using Process process = Process.Start(start)!;
        process.WaitForExit(60000);
        Assert.AreEqual(0, process.ExitCode, $"git {arguments}");
    }

    /// <summary>Build output of ProcBridge.Launcher (same configuration as the tests).</summary>
    private static string LauncherOutput()
    {
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}") ? "Debug" : "Release";
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "client", "ProcBridge.Launcher", "bin", configuration, "net10.0");
            if (File.Exists(Path.Combine(candidate, "ProcBridge.Launcher.dll")))
                return candidate;
        }
        throw new DirectoryNotFoundException("ProcBridge.Launcher build output not found.");
    }

    private static void CopyFolder(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
