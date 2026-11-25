using System.Diagnostics;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M4 acceptance (plan 5, 12): children and grandchildren of the hooked process are hooked too, across
/// architectures, whatever environment their creator passes; the Job Object holds the whole tree.
/// </summary>
[TestClass]
public sealed class ProcessTreeTests
{
    private static readonly TimeSpan _treeTimeout = TimeSpan.FromSeconds(60);

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void Grandchild_ReadsThroughVirtualFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"cmd.exe /d /c cmd.exe /d /c cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64", "SysWOW64")]
    [DataRow("Win32", "Sysnative")]
    public void ChildOfOtherArchitecture_ReadsThroughVirtualFolder(string platform, string systemFolder)
    {
        using HookSandbox sandbox = new(platform);
        string otherCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), systemFolder, "cmd.exe");

        // cmd (same architecture as Spawn) -> cmd of the other architecture -> cmd of the other architecture.
        ProcessResult result = sandbox.RunHooked(
            $"cmd.exe /d /c \"\"{otherCmd}\" /d /c cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void ChildCreatedWithoutConfigurationVariable_IsStillHooked(string platform)
    {
        // The parent clears PROCBRIDGE_MAP, so the child's environment block does not carry it: the
        // configuration must reach the child through the init block.
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked(
            $"cmd.exe /d /c \"set PROCBRIDGE_MAP=& cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void InheritedHandle_KeepsVirtualName(string platform)
    {
        // cmd opens the virtual file for "<" and the probe inherits it as standard input.
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked(
            $"cmd.exe /d /c \"\"{sandbox.Binaries.Probe}\" --stdin-name < \"{sandbox.VirtualRoot}\\a.txt\"\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual($"stdin: \\\\?\\{sandbox.VirtualRoot}\\a.txt", result.StandardOutput.TrimEnd());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void Spawn_WaitsForWholeTree(string platform)
    {
        // The first process exits at once; a detached grandchild writes the marker two seconds later.
        using HookSandbox sandbox = new(platform);
        string marker = Path.Combine(sandbox.RealRoot, "marker.txt");

        using Process spawn = sandbox.StartHooked(
            $"cmd.exe /d /c start \"\" /b powershell.exe -NoProfile -NonInteractive -Command "
            + $"\"Start-Sleep -Seconds 2; Set-Content -LiteralPath '{sandbox.VirtualRoot}\\marker.txt' -Value done\"");

        Assert.IsTrue(spawn.WaitForExit(_treeTimeout), "Spawn did not exit.");
        Assert.AreEqual(0, spawn.ExitCode);
        Assert.IsTrue(File.Exists(marker), "Spawn returned before the grandchild finished.");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void KillingSpawn_KillsWholeTree(string platform)
    {
        using HookSandbox sandbox = new(platform);
        string pidFile = Path.Combine(sandbox.RealRoot, "pid.txt");

        using Process spawn = sandbox.StartHooked(
            $"cmd.exe /d /c powershell.exe -NoProfile -NonInteractive -Command "
            + $"\"Set-Content -LiteralPath '{sandbox.VirtualRoot}\\pid.txt' -Value $PID; Start-Sleep -Seconds 120\"");
        int grandchildId = WaitForPid(pidFile, spawn);

        spawn.Kill(entireProcessTree: false);
        spawn.WaitForExit();

        Assert.IsTrue(WaitForProcessExit(grandchildId, _treeTimeout), "The grandchild survived the launcher (job not closed).");
    }

    [TestMethod]
    public void DotNetBuild_InsideVirtualFolder_BuildsWithChildProcesses()
    {
        // dotnet -> MSBuild worker node (-m) -> csc (UseSharedCompilation=false runs it as its own
        // process). Build servers that would outlive the tree outside the job are disabled.
        using HookSandbox sandbox = new("x64");
        CreateDotNetSolution(sandbox.RealRoot);
        Dictionary<string, string> environment = new()
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

        ProcessResult build = sandbox.RunHooked(
            $"dotnet.exe build \"{sandbox.VirtualRoot}\\app\\app.csproj\" -m:2 -nodeReuse:false --disable-build-servers "
            + "-p:UseSharedCompilation=false -clp:NoSummary",
            environment);

        Assert.AreEqual(0, build.ExitCode, build.StandardOutput + build.StandardError);
        Assert.Contains($"app -> {sandbox.VirtualRoot}\\app\\bin\\Debug\\net10.0\\app.dll", build.StandardOutput);
        // csc wrote through the virtual path, so the output landed in the real folder.
        Assert.IsTrue(File.Exists(Path.Combine(sandbox.RealRoot, "app", "bin", "Debug", "net10.0", "app.dll")), build.StandardOutput);

        ProcessResult run = sandbox.RunHooked($"dotnet.exe \"{sandbox.VirtualRoot}\\app\\bin\\Debug\\net10.0\\app.dll\"", environment);

        Assert.AreEqual(0, run.ExitCode, run.StandardError);
        Assert.AreEqual("hello from lib", run.StandardOutput.TrimEnd());
    }

    /// <summary>App + library, isolated from the repository's Directory.*.props and central packages.</summary>
    private static void CreateDotNetSolution(string root)
    {
        foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            File.WriteAllText(Path.Combine(root, name), "<Project />\r\n");
        }
        Directory.CreateDirectory(Path.Combine(root, "lib"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "lib", "lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\r\n</Project>\r\n");
        File.WriteAllText(Path.Combine(root, "lib", "Greeting.cs"),
            "namespace Lib;\r\npublic static class Greeting { public static string Text => \"hello from lib\"; }\r\n");
        File.WriteAllText(Path.Combine(root, "app", "app.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n"
            + "  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\r\n"
            + "  <ItemGroup><ProjectReference Include=\"..\\lib\\lib.csproj\" /></ItemGroup>\r\n"
            + "</Project>\r\n");
        File.WriteAllText(Path.Combine(root, "app", "Program.cs"), "System.Console.WriteLine(Lib.Greeting.Text);\r\n");
    }

    private static int WaitForPid(string pidFile, Process spawn)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < _treeTimeout)
        {
            if (spawn.HasExited)
            {
                Assert.Fail($"Spawn exited early with {spawn.ExitCode}.");
            }
            try
            {
                string text = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(text, out int pid))
                {
                    return pid;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not written yet (or still being written).
            }
            Thread.Sleep(100);
        }
        spawn.Kill(entireProcessTree: true);
        Assert.Fail("The grandchild never wrote its process id.");
        return 0;
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.WaitForExit(timeout))
            {
                return true;
            }
            process.Kill();
            return false;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }
}
