using ProcBridge.Launcher;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M3 acceptance (plan section 13): writes through a writable virtual folder served by an Agent on loopback behave like
/// writes to a real folder, and land on machine B.
/// </summary>
[TestClass]
public sealed class RemoteWriteTests
{
    private static string Lines(ProcessResult result) => result.StandardOutput + result.StandardError;

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task WriteScenario_ThroughAgent_MatchesRealFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"--write \"{sandbox.RealRoot}\\w-real\"");
        ProcessResult remote = session.Run($"\"{sandbox.Binaries.Probe}\" --write \"{sandbox.VirtualRoot}\\w-remote\"");

        Assert.AreEqual(0, real.ExitCode, Lines(real));
        Assert.AreEqual(0, remote.ExitCode, Lines(remote));
        Assert.Contains("Write.read saved: size=9 text=[version 3]", real.StandardOutput);
        HookComparisonTests.AssertSameLines(real.StandardOutput, remote.StandardOutput);
        // Everything landed on machine B (the real folder behind the share).
        Assert.AreEqual("version 3", File.ReadAllText(Path.Combine(sandbox.RealRoot, "w-remote", "d.txt")));
        Assert.AreEqual("sup", File.ReadAllText(Path.Combine(sandbox.RealRoot, "w-remote", "c.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(sandbox.RealRoot, "w-remote", "e.txt")));
        Assert.AreEqual(0, session.Broker.WriteBack.PendingCount);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task CmdRedirectAndPowerShellReplace_LandOnMachineB(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);
        string v = sandbox.VirtualRoot;

        ProcessResult cmd = session.Run($"cmd.exe /d /c \"echo first> \"{v}\\notes.txt\" && echo second>> \"{v}\\notes.txt\" && mkdir \"{v}\\made\" && type \"{v}\\notes.txt\"\"");
        // Editors save to a temp file and swap it in (File.Replace keeps a backup here).
        string script = $"[IO.File]::WriteAllText('{v}\\notes.tmp', 'saved by editor');"
            + $"[IO.File]::Replace('{v}\\notes.tmp', '{v}\\notes.txt', '{v}\\notes.bak');"
            + $"[IO.File]::ReadAllText('{v}\\notes.txt');"
            + $"(Get-ChildItem '{v}' -Name | Sort-Object) -join ','";
        ProcessResult ps = session.Run($"powershell.exe -NoProfile -NonInteractive -Command \"{script}\"");

        Assert.AreEqual(0, cmd.ExitCode, Lines(cmd));
        // cmd keeps the space before "&&" in the echoed text.
        Assert.AreEqual("first \r\nsecond \r\n", cmd.StandardOutput);
        Assert.IsTrue(Directory.Exists(Path.Combine(sandbox.RealRoot, "made")));
        Assert.AreEqual(0, ps.ExitCode, Lines(ps));
        string[] lines = ps.StandardOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual("saved by editor", lines[0], Lines(ps));
        StringAssert.Contains(lines[1], "notes.bak,notes.txt");
        Assert.DoesNotContain("notes.tmp", lines[1]);
        Assert.AreEqual("saved by editor", File.ReadAllText(Path.Combine(sandbox.RealRoot, "notes.txt")));
        Assert.AreEqual("first \r\nsecond \r\n", File.ReadAllText(Path.Combine(sandbox.RealRoot, "notes.bak")));
        Assert.IsFalse(File.Exists(Path.Combine(sandbox.RealRoot, "notes.tmp")));
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ChildWritingInheritedHandleAfterParentClosed_LandsOnMachineB(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --child inherit-write \"{sandbox.VirtualRoot}\\inherited.txt\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        // Checked before the session ends: the child's own close wrote it back, not the final sync of open files.
        Assert.AreEqual("parent+child", File.ReadAllText(Path.Combine(sandbox.RealRoot, "inherited.txt")));
        Assert.AreEqual(0, session.Broker.WriteBack.PendingCount);
    }

    [TestMethod]
    public async Task WriteThrough_SendsEachWriteBeforeClose()
    {
        using HookSandbox sandbox = new("x64");
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true, writeThrough: true);
        string file = Path.Combine(sandbox.RealRoot, "live.txt");
        // PowerShell checks machine B's copy while its own handle is still open.
        string script = $"$s = [IO.File]::Open('{sandbox.VirtualRoot}\\live.txt', 'CreateNew', 'Write', 'ReadWrite');"
            + "$b = [Text.Encoding]::ASCII.GetBytes('live'); $s.Write($b, 0, 4); $s.Flush();"
            + $"[IO.File]::ReadAllText('{file}'); $s.Dispose()";

        ProcessResult result = session.Run($"powershell.exe -NoProfile -NonInteractive -Command \"{script}\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        Assert.AreEqual("live", result.StandardOutput.Trim());
    }

    [TestMethod]
    public async Task DotNetBuild_WithOutputInsideVirtualFolder_WritesToMachineB()
    {
        // M2 needed BaseOutputPath outside the virtual folder; with writes bin and obj go to machine B.
        using HookSandbox sandbox = new("x64");
        RemoteProcessTreeTests.CreateDotNetProject(sandbox.RealRoot);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);
        Dictionary<string, string> environment = new(LaunchSession.BuildServerVariables)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

        ProcessResult build = session.Run(
            $"dotnet.exe build \"{sandbox.VirtualRoot}\\app\\app.csproj\" -m:2 -nodeReuse:false --disable-build-servers -clp:NoSummary",
            environment);

        Assert.AreEqual(0, build.ExitCode, Lines(build));
        Assert.Contains($"app -> {sandbox.VirtualRoot}\\app\\bin\\Debug\\net10.0\\app.dll", build.StandardOutput);
        string app = Path.Combine(sandbox.RealRoot, "app", "bin", "Debug", "net10.0", "app.dll");
        Assert.IsTrue(File.Exists(app), "the build output is on machine B");
        Assert.AreEqual(0, session.Broker.WriteBack.PendingCount);

        ProcessResult run = sandbox.Run("dotnet.exe", $"\"{app}\"");

        Assert.AreEqual(0, run.ExitCode, run.StandardError);
        Assert.AreEqual("hello from machine B", run.StandardOutput.TrimEnd());
    }
}
