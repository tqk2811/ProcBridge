using System.Text;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M6 acceptance (plan section 13: "M4 through the relay"): the M1 comparison and a process tree with the virtual
/// folder served by an Agent that the Broker reaches only through the relay, and what the relay forwards is
/// ciphertext only.
/// </summary>
[TestClass]
public sealed class RelayHookTests
{
    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task Probe_ThroughRelay_MatchesRealFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RelaySession session = await RelaySession.StartAsync(sandbox);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"\"{sandbox.RealRoot}\"");
        ProcessResult remote = session.Run($"\"{sandbox.Binaries.Probe}\" \"{sandbox.VirtualRoot}\"");

        Assert.AreEqual(0, real.ExitCode, real.StandardError);
        Assert.AreEqual(0, remote.ExitCode, remote.StandardError);
        Assert.Contains("ReadFile: <ROOT>\\a.txt -> size=", real.StandardOutput);
        HookComparisonTests.AssertSameLines(real.StandardOutput, remote.StandardOutput);
        Assert.AreEqual(0, session.Agent.GetConnections().Count(x => !x.RemoteAddress.StartsWith("relay:", StringComparison.Ordinal)),
            "A connection reached the Agent outside the relay.");
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task Grandchild_ReadsThroughRelay_RelaySeesOnlyCiphertext(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RelaySession session = await RelaySession.StartAsync(sandbox);

        ProcessResult result = session.Run($"cmd.exe /d /c cmd.exe /d /c type \"{sandbox.VirtualRoot}\\a.txt\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(HookSandbox.FileAContent, result.StandardOutput);

        // Everything the relay forwarded on data lanes: the file content and the file and share names never appear.
        byte[] captured = session.Relay.Sockets.CapturedLaneBytes;
        Assert.IsGreaterThan(0, session.Relay.Sockets.LaneCount);
        Assert.IsGreaterThan(HookSandbox.FileAContent.Length, captured.Length);
        AssertAbsent(captured, Encoding.UTF8.GetBytes("hello from the real folder"), "file content");
        AssertAbsent(captured, Encoding.Unicode.GetBytes("a.txt"), "file name (UTF-16)");
        AssertAbsent(captured, Encoding.UTF8.GetBytes("a.txt"), "file name (UTF-8)");
        AssertAbsent(captured, Encoding.UTF8.GetBytes("share"), "share name");
    }

    private static void AssertAbsent(byte[] haystack, byte[] needle, string what)
        => Assert.AreEqual(-1, haystack.AsSpan().IndexOf(needle), $"The relay saw the {what} in clear.");
}
