using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Launcher;
using ProcBridge.Launcher.Enums;
using ProcBridge.Launcher.Models;
using ProcBridge.Protocol.Enums;
using ProcBridge.Tests.Secure;

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
        Assert.IsNull(options.WorkingDirectory);
    }

    [TestMethod]
    public void CommandLine_WorkingDirectory_IsParsed()
    {
        LauncherOptions options = LauncherCommandLine.Parse(
            ["--agent", "host", "--share", "src", "--map", @"V:\a", "", "--cwd", @"C:\Work", "--", "cmd.exe"]);

        Assert.AreEqual(@"C:\Work", options.WorkingDirectory);
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
    public void CommandLine_ConfigFile_GivesTheOptions_AndTheLineOverridesIt()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pb-config-{Guid.NewGuid():N}")).FullName;
        try
        {
            string file = Path.Combine(folder, "run.json");
            File.WriteAllText(file, """
                {
                  // comments and trailing commas are fine
                  "agent": "10.0.0.5:5000",
                  "share": "src",
                  "maps": [{ "virtual": "Proj", "remote": "proj" }],
                  "noBuildServers": true,
                  "conflict": "overwrite",
                  "cwd": "work",
                  "command": ["cmd.exe", "/c", "dir x"],
                }
                """);

            LauncherOptions fromFile = LauncherCommandLine.Parse(["--config", file]);
            LauncherOptions overridden = LauncherCommandLine.Parse(["--config", file, "--share", "other", "--map", @"V:\b", "b", "--", "git", "status"]);

            Assert.AreEqual("10.0.0.5", fromFile.AgentHost);
            Assert.AreEqual(5000, fromFile.AgentPort);
            Assert.AreEqual("src", fromFile.ShareName);
            // Relative paths start at the file's folder.
            Assert.AreEqual(Path.Combine(folder, "Proj"), fromFile.Mappings.Single().VirtualRoot);
            Assert.AreEqual(Path.Combine(folder, "work"), fromFile.WorkingDirectory);
            Assert.IsTrue(fromFile.DisableBuildServers);
            Assert.AreEqual(ConflictPolicy.Overwrite, fromFile.ConflictPolicy);
            Assert.AreEqual("cmd.exe /c \"dir x\"", fromFile.CommandLine);
            Assert.AreEqual("other", overridden.ShareName);
            CollectionAssert.AreEqual(new[] { "proj", "b" }, overridden.Mappings.Select(x => x.RemotePath).ToArray());
            Assert.AreEqual("git status", overridden.CommandLine);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("""{ "agnet": "host" }""")]
    [DataRow("""{ "agent": """)]
    public void CommandLine_ConfigFileWithTypoOrBadJson_IsRefused(string json)
    {
        string file = Path.Combine(Path.GetTempPath(), $"pb-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        try
        {
            var e = Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(["--config", file, "--", "cmd.exe"]));
            StringAssert.Contains(e.Message, "--config");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void CommandLine_ConfigFile_FalseFlagsAndNegations_AndWarnings()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pb-config-{Guid.NewGuid():N}")).FullName;
        try
        {
            string first = Path.Combine(folder, "a.json");
            string second = Path.Combine(folder, "b.json");
            File.WriteAllText(first, """{ "agent": "h", "share": "s", "maps": [{ "virtual": "V", "remote": "" }], "plaintext": true, "writeThrough": true, "noBuildServers": true, "command": ["one.exe"] }""");
            File.WriteAllText(second, """{ "writeThrough": false, "command": ["two.exe"] }""");
            var warnings = new List<string>();

            LauncherOptions options = LauncherCommandLine.Parse(["--config", first, "--config", second, "--no-plaintext", "--build-servers"], warnings.Add);

            Assert.IsFalse(options.AllowPlaintextTransport);
            Assert.IsFalse(options.WriteThrough, "false in the later file undoes true of the first");
            Assert.IsFalse(options.DisableBuildServers);
            Assert.AreEqual("two.exe", options.CommandLine);
            Assert.HasCount(2, warnings);
            Assert.IsTrue(warnings.Any(x => x.Contains("plaintext")));
            Assert.IsTrue(warnings.Any(x => x.Contains("several")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("../secret")]
    [DataRow("proj/../../x")]
    [DataRow(@"C:\Windows")]
    [DataRow("V:Proj")]
    [DataRow(@"\root")]
    public void CommandLine_RemoteOutsideTheShare_IsRefused(string remote)
    {
        string file = Path.Combine(Path.GetTempPath(), $"pb-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { agent = "h", share = "s", maps = new[] { new { @virtual = @"V:\a", remote } } }));
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(["--config", file, "--", "cmd.exe"]));
            Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(["--agent", "h", "--share", "s", "--map", @"V:\a", remote, "--", "cmd.exe"]));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Machine B revoked this machine: the run says to pair again, not that the Agent is unreachable.</summary>
    [TestMethod]
    public async Task Run_RevokedOnMachineB_FailsAsNotPaired()
    {
        using HookSandbox sandbox = new("x64");
        using var peers = new SecurePeers(paired: false);
        peers.BrokerTrust.Add(peers.AgentKey.PublicKey, "machine-b", "127.0.0.1");
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 }, peers.Agent());
        agent.Start();
        var options = new LauncherOptions
        {
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Security = peers.Broker,
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            CommandLine = "cmd.exe /d /c exit 0",
        };

        var e = await Assert.ThrowsExactlyAsync<LaunchFailedException>(() => new LaunchSession(options).RunAsync());

        Assert.AreEqual(LaunchFailureReason.NotPaired, e.Reason, e.Message);
    }

    [TestMethod]
    public void CommandLine_MissingConfigFile_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(["--config", @"Z:\none\run.json"]));
        Assert.ThrowsExactly<ArgumentException>(() => LauncherCommandLine.Parse(["--config"]));
    }

    /// <summary>An Agent that does not answer stops the run with that reason before Spawn (it used to say "inject failed").</summary>
    [TestMethod]
    public async Task Run_AgentNotReachable_FailsBeforeTheTargetStarts()
    {
        using HookSandbox sandbox = new("x64");
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        string marker = Path.Combine(sandbox.BaseDirectory, "ran.txt");
        var options = new LauncherOptions
        {
            AllowPlaintextTransport = true,
            AgentHost = "127.0.0.1",
            AgentPort = port,
            ShareName = "share",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            CommandLine = $"cmd.exe /d /c echo x> \"{marker}\"",
        };

        var e = await Assert.ThrowsExactlyAsync<LaunchFailedException>(() => new LaunchSession(options).RunAsync());

        Assert.AreEqual(LaunchFailureReason.AgentUnreachable, e.Reason);
        StringAssert.Contains(e.Message, $"127.0.0.1:{port}");
        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task Run_UnknownShare_FailsBeforeTheTargetStarts()
    {
        using HookSandbox sandbox = new("x64");
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        var options = new LauncherOptions
        {
            AllowPlaintextTransport = true,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "nope",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            CommandLine = "cmd.exe /d /c exit 0",
        };

        var e = await Assert.ThrowsExactlyAsync<LaunchFailedException>(() => new LaunchSession(options).RunAsync());

        Assert.AreEqual(LaunchFailureReason.ShareUnknown, e.Reason);
        Assert.AreEqual("nope", e.Target);
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
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string marker = Path.Combine(sandbox.BaseDirectory, "copied.txt");
        var options = new LauncherOptions
        {
            AllowPlaintextTransport = true,
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

    /// <summary>A working directory inside a virtual folder exists on machine B only: the hook of the target enters it.</summary>
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task Run_WorkingDirectoryInAVirtualFolder_StartsTheTargetThere(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string workingDirectory = Path.Combine(sandbox.VirtualRoot, "sub");
        var options = new LauncherOptions
        {
            AllowPlaintextTransport = true,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            WorkingDirectory = workingDirectory,
            // A relative name resolves against the virtual folder; the child cmd inherits the folder.
            CommandLine = "cmd.exe /d /c cd & type b.txt & cmd.exe /d /c cd & cmd.exe /d /c \"if defined PROCBRIDGE_CWD (echo inherited) else (echo clear)\"",
        };
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

        int exitCode = await new LaunchSession(options, lines.Enqueue).RunAsync();

        Assert.AreEqual(0, exitCode, string.Join(Environment.NewLine, lines));
        // The variable carrying the folder is for the target only: its child must not enter the folder again.
        CollectionAssert.AreEqual(new[] { workingDirectory, "bee", workingDirectory, "clear" }, lines.ToArray());
    }

    [TestMethod]
    public async Task Run_WorkingDirectoryMissingOnMachineB_FailsBeforeTheTargetStarts()
    {
        using HookSandbox sandbox = new("x64");
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string missing = Path.Combine(sandbox.VirtualRoot, "nope");
        var options = new LauncherOptions
        {
            AllowPlaintextTransport = true,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            WorkingDirectory = missing,
            CommandLine = "cmd.exe /d /c exit 0",
        };

        var e = await Assert.ThrowsExactlyAsync<LaunchFailedException>(() => new LaunchSession(options).RunAsync());

        Assert.AreEqual(LaunchFailureReason.WorkingDirectoryMissing, e.Reason);
        Assert.AreEqual(missing, e.Target);
    }

    [TestMethod]
    [DataRow(@"V:\Proj", "proj")]
    [DataRow(@"V:\Proj\src\app\", "proj/src/app")]
    [DataRow(@"V:\Proj\Lib", "lib")]
    [DataRow(@"V:\All\x", "x")]
    [DataRow(@"V:\Projects", null)]
    [DataRow(@"C:\Work", null)]
    public void ToRemotePath_UsesTheInnermostMapping(string localPath, string? expected)
    {
        Broker.Models.VirtualMapping[] mappings =
        [
            new(@"V:\Proj", "proj"),
            new(@"V:\Proj\Lib\", "/lib/"),
            new(@"V:\All", ""),
        ];

        Assert.AreEqual(expected, LaunchSession.ToRemotePath(mappings, localPath));
    }

    /// <summary>The WPF Launcher path: output lines of the tree reach the sink, the target starts in the working directory.</summary>
    [TestMethod]
    public async Task Run_WithOutputSink_CollectsLinesOfTheTree_InTheWorkingDirectory()
    {
        using HookSandbox sandbox = new("x64");
        await using var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string workingDirectory = Directory.CreateDirectory(Path.Combine(sandbox.BaseDirectory, "work")).FullName;
        var options = new LauncherOptions
        {
            AllowPlaintextTransport = true,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Mappings = [new Broker.Models.VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            SpawnPath = sandbox.Binaries.Spawn,
            WorkingDirectory = workingDirectory,
            // The child cmd proves the redirected streams reach the whole tree.
            CommandLine = $"cmd.exe /d /c cd & cmd.exe /d /c type \"{sandbox.VirtualRoot}\\sub\\b.txt\"",
        };
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

        int exitCode = await new LaunchSession(options, lines.Enqueue).RunAsync();

        Assert.AreEqual(0, exitCode);
        CollectionAssert.AreEqual(new[] { workingDirectory, "bee" }, lines.ToArray());
    }
}
