using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Launcher;
using ProcBridge.Launcher.Models;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Tests.Native;

/// <summary>ProcBridge.Launcher: command line, Broker hosting and Spawn with the session.</summary>
[TestClass]
public sealed class LauncherTests
{
    [TestMethod]
    public void CommandLine_IsParsed()
    {
        LauncherOptions options = LauncherCommandLine.Parse(
            ["--agent", "10.0.0.5:5000", "--share", "src", "--map", @"V:\Remote\Proj\", "proj", "--arch", "x86", "--", "cmd.exe", "/c", "dir x"]);

        Assert.AreEqual("10.0.0.5", options.AgentHost);
        Assert.AreEqual(5000, options.AgentPort);
        Assert.AreEqual("src", options.ShareName);
        Assert.AreEqual(@"V:\Remote\Proj", options.Mappings.Single().VirtualRoot);
        Assert.AreEqual("proj", options.Mappings.Single().RemotePath);
        StringAssert.EndsWith(options.SpawnPath, "ProcBridge.Spawn.x86.exe");
        Assert.AreEqual("cmd.exe /c \"dir x\"", options.CommandLine);
    }

    [TestMethod]
    public void CommandLine_WriteOptions_AreParsed()
    {
        LauncherOptions defaults = LauncherCommandLine.Parse(["--agent", "h", "--share", "s", "--map", @"V:\a", "", "--", "cmd.exe"]);
        LauncherOptions options = LauncherCommandLine.Parse(
            ["--agent", "h", "--share", "s", "--map", @"V:\a", "", "--write-through", "--conflict", "overwrite", "--", "cmd.exe"]);

        Assert.IsFalse(defaults.WriteThrough);
        Assert.AreEqual(ConflictPolicy.KeepBoth, defaults.ConflictPolicy);
        Assert.IsTrue(options.WriteThrough);
        Assert.AreEqual(ConflictPolicy.Overwrite, options.ConflictPolicy);
        Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(["--agent", "h", "--share", "s", "--map", @"V:\a", "", "--conflict", "x", "--", "cmd.exe"]));
    }

    [TestMethod]
    [DataRow("--share", "src", "--map", @"V:\a", "", "--", "cmd.exe")]
    [DataRow("--agent", "host", "--share", "src", "--", "cmd.exe")]
    [DataRow("--agent", "host", "--share", "src", "--map", @"V:\a", "")]
    [DataRow("--agent", "host", "--share", "src", "--map", @"V:\a", "", "--arch", "arm", "--", "cmd.exe")]
    public void CommandLine_Incomplete_IsRefused(params string[] args)
    {
        Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(args));
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task Run_ReadsThroughAgent_AndStopsBrokerAfterwards(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string marker = Path.Combine(sandbox.BaseDirectory, "copied.txt");
        var options = new LauncherOptions
        {
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            // The copy lands outside the virtual folder, so it proves the read went through the Agent.
            CommandLine = $"cmd.exe /d /c copy /y \"{sandbox.VirtualRoot}\\sub\\b.txt\" \"{marker}\" >nul",
        };

        var session = new LaunchSession(options);
        int exitCode = await session.RunAsync();

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("bee" + Environment.NewLine, File.ReadAllText(marker));
        // The run's Broker is stopped: its pipe is gone.
        string pipeName = ProcBridge.HookIpc.HookIpcProtocol.GetPipeName(session.SessionId);
        Assert.IsFalse(Directory.GetFiles(@"\\.\pipe\").Any(x => x.EndsWith(pipeName, StringComparison.OrdinalIgnoreCase)));
    }
}
