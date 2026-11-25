using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Launcher;
using ProcBridge.Launcher.Models;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M4 with machine B (plan 5, 12): the process tree under a Broker session. Children get the session through the
/// init block, inherited remote handles keep machine B's metadata, and a build tree reads its sources over the
/// Agent (loopback).
/// </summary>
[TestClass]
public sealed class RemoteProcessTreeTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task Grandchild_ReadsThroughAgent(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult result = session.Run($"cmd.exe /d /c cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64", "SysWOW64")]
    [DataRow("Win32", "Sysnative")]
    public async Task ChildOfOtherArchitecture_ReadsThroughAgent(string platform, string systemFolder)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string otherCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), systemFolder, "cmd.exe");

        // The parent clears the session variable: the child must get it from the init block.
        ProcessResult result = session.Run(
            $"cmd.exe /d /c \"set PROCBRIDGE_SESSION=& \"{otherCmd}\" /d /c type \"{sandbox.VirtualRoot}\\sub\\b.txt\"\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("bee" + Environment.NewLine, result.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task InheritedRemoteHandle_KeepsMachineBMetadata(string platform)
    {
        // cmd opens the virtual file for "<" (a handle to the cache copy) and the probe inherits it: the file id
        // and times it reports must be machine B's, not the cache copy's.
        using HookSandbox sandbox = new(platform);
        File.SetLastWriteTimeUtc(Path.Combine(sandbox.RealRoot, "a.txt"), new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"--file-id \"{sandbox.RealRoot}\\a.txt\"");
        ProcessResult name = session.Run($"cmd.exe /d /c \"\"{sandbox.Binaries.Probe}\" --stdin-name < \"{sandbox.VirtualRoot}\\a.txt\"\"");
        ProcessResult inherited = session.Run($"cmd.exe /d /c \"\"{sandbox.Binaries.Probe}\" --stdin-id < \"{sandbox.VirtualRoot}\\a.txt\"\"");

        Assert.AreEqual(0, real.ExitCode, real.StandardOutput);
        Assert.AreEqual(0, name.ExitCode, name.StandardOutput + name.StandardError);
        Assert.AreEqual($"stdin: \\\\?\\{sandbox.VirtualRoot}\\a.txt", name.StandardOutput.TrimEnd());
        Assert.AreEqual(0, inherited.ExitCode, inherited.StandardOutput + inherited.StandardError);
        Assert.AreEqual(real.StandardOutput, inherited.StandardOutput);
    }

    [TestMethod]
    public async Task DotNetBuild_OfProjectOnAgent_BuildsWithChildProcesses()
    {
        // Machine B is read-only for now (M3 brings writes): bin and obj go to a local folder outside the virtual
        // one. dotnet -> MSBuild worker node (-m) -> csc (UseSharedCompilation=false: its own process).
        using HookSandbox sandbox = new("x64");
        CreateDotNetProject(sandbox.RealRoot);
        string output = Path.Combine(sandbox.BaseDirectory, "build");
        await using RemoteSession session = RemoteSession.Start(sandbox);
        Dictionary<string, string> environment = new(LaunchSession.BuildServerVariables)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

        ProcessResult build = session.Run(
            $"dotnet.exe build \"{sandbox.VirtualRoot}\\app\\app.csproj\" -m:2 -nodeReuse:false --disable-build-servers -clp:NoSummary "
            + $"\"-p:BaseOutputPath={output}\\bin\\\\\" \"-p:BaseIntermediateOutputPath={output}\\obj\\\\\"",
            environment);

        Assert.AreEqual(0, build.ExitCode, build.StandardOutput + build.StandardError);
        string app = Path.Combine(output, "bin", "Debug", "net10.0", "app.dll");
        Assert.IsTrue(File.Exists(app), build.StandardOutput);
        // The project was read through the virtual folder (sources came over the Agent).
        Assert.Contains($"app -> {app}", build.StandardOutput);
        Assert.IsFalse(Directory.Exists(Path.Combine(sandbox.RealRoot, "app", "obj")), "The build wrote into machine B's folder.");

        ProcessResult run = sandbox.Run("dotnet.exe", $"\"{app}\"");

        Assert.AreEqual(0, run.ExitCode, run.StandardError);
        Assert.AreEqual("hello from machine B", run.StandardOutput.TrimEnd());
    }

    [TestMethod]
    public void CommandLine_NoBuildServers_IsParsed()
    {
        LauncherOptions options = LauncherCommandLine.Parse(
            ["--agent", "host", "--share", "src", "--map", @"V:\a", "", "--no-build-servers", "--", "cmd.exe"]);

        Assert.IsTrue(options.DisableBuildServers);
    }

    [TestMethod]
    public async Task Launcher_WaitsForWholeTree_AndSetsBuildServerVariables()
    {
        // The target exits at once; a detached grandchild writes the marker (outside the virtual folder) two seconds
        // later with the variables it got.
        using HookSandbox sandbox = new("x64");
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string marker = Path.Combine(sandbox.BaseDirectory, "marker.txt");
        var options = new LauncherOptions
        {
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            DisableBuildServers = true,
            CommandLine = "cmd.exe /d /c start \"\" /b powershell.exe -NoProfile -NonInteractive -Command "
                + $"\"Start-Sleep -Seconds 2; Set-Content -LiteralPath '{marker}' -Value ($env:DOTNET_CLI_USE_MSBUILD_SERVER + $env:MSBUILDDISABLENODEREUSE + $env:UseSharedCompilation)\"",
        };

        int exitCode = await new LaunchSession(options).RunAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(marker), "The Launcher returned before the grandchild finished.");
        Assert.AreEqual("01false", File.ReadAllText(marker).Trim());
    }

    /// <summary>One console project, isolated from the repository's Directory.*.props and central packages.</summary>
    internal static void CreateDotNetProject(string root)
    {
        foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            File.WriteAllText(Path.Combine(root, name), "<Project />\r\n");
        }
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "app.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n"
            + "  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\r\n"
            + "</Project>\r\n");
        File.WriteAllText(Path.Combine(root, "app", "Program.cs"), "System.Console.WriteLine(\"hello from machine B\");\r\n");
    }
}
