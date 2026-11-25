using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M2 acceptance (plan section 13): the M1 checks again, with the virtual folder served by an Agent on loopback
/// through the Broker instead of a local folder.
/// </summary>
[TestClass]
public sealed class RemoteHookTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task Probe_ThroughAgent_MatchesRealFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"\"{sandbox.RealRoot}\"");
        ProcessResult remote = session.Run($"\"{sandbox.Binaries.Probe}\" \"{sandbox.VirtualRoot}\"");

        Assert.AreEqual(0, real.ExitCode, real.StandardError);
        Assert.AreEqual(0, remote.ExitCode, remote.StandardError);
        Assert.Contains("ReadFile: <ROOT>\\a.txt -> size=", real.StandardOutput);
        HookComparisonTests.AssertSameLines(real.StandardOutput, remote.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task CmdTypeAndDir_ReadThroughAgent(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult type = session.Run($"cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"");
        ProcessResult real = sandbox.Run("cmd.exe", $"/d /c dir /b /a /s \"{sandbox.RealRoot}\"");
        ProcessResult dir = session.Run($"cmd.exe /d /c dir /b /a /s \"{sandbox.VirtualRoot}\"");

        Assert.AreEqual(0, type.ExitCode, type.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, type.StandardOutput);
        Assert.AreEqual(0, dir.ExitCode, dir.StandardError);
        Assert.AreEqual(real.StandardOutput.Replace(sandbox.RealRoot, sandbox.VirtualRoot), dir.StandardOutput);
        Assert.Contains("c.bin", dir.StandardOutput);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task CmdCd_WorksInsideRemoteFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult result = session.Run($"cmd.exe /d /c \"cd /d \"{sandbox.VirtualRoot}\\sub\" && cd && type b.txt && dir /b deep\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string[] lines = result.StandardOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[] { sandbox.VirtualRoot + "\\sub", "bee", "c.bin" }, lines);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ProcessExit_WithRemoteHandlesOpen_DoesNotCrash(string platform)
    {
        // The current directory handle stays open until exit. Global destructors used to close the Broker pipes
        // through the NtClose hook after its tables were destroyed: an access violation at exit, in some runs.
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        for (int i = 0; i < 20; i++)
        {
            ProcessResult result = session.Run($"cmd.exe /d /c \"cd /d \"{sandbox.VirtualRoot}\\sub\" && cd\"");
            Assert.AreEqual(0, result.ExitCode, $"run {i}: 0x{result.ExitCode:X8} {result.StandardError}");
        }
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task DotNetFramework_ReadAllText_ReadsThroughAgent(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string script = $"[IO.File]::ReadAllText('{sandbox.VirtualRoot}\\a.txt');"
            + $"[IO.Directory]::GetFiles('{sandbox.VirtualRoot}\\sub\\deep');"
            + $"(Get-Item '{sandbox.VirtualRoot}\\sub\\b.txt').Length";

        ProcessResult result = session.Run($"powershell.exe -NoProfile -NonInteractive -Command \"{script}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string[] lines = result.StandardOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[] { "hello from the real folder", sandbox.VirtualRoot + "\\sub\\deep\\c.bin", "5" }, lines);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ReadOnlyShare_RefusesWritesWithWriteProtect(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult result = session.Run($"cmd.exe /d /c \"echo x> \"{sandbox.VirtualRoot}\\new.txt\"\"");

        Assert.AreNotEqual(0, result.ExitCode);
        // ERROR_WRITE_PROTECT text from cmd; nothing reaches the real folder.
        Assert.IsFalse(File.Exists(Path.Combine(sandbox.RealRoot, "new.txt")));
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task PipeOwnedByAnotherProcess_IsRefused(string platform)
    {
        // The pipe is served by this test process; the hook is told the Broker is someone else.
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);

        ProcessResult result = session.RunWithBrokerProcess(Environment.ProcessId + 4, $"cmd.exe /d /c echo started");

        Assert.AreEqual(125, result.ExitCode, result.StandardOutput);
        Assert.Contains("hook initialization failed (error 5)", result.StandardError);
        Assert.DoesNotContain("started", result.StandardOutput);
    }
}
