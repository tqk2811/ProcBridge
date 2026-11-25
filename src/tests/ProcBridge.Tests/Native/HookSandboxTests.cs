namespace ProcBridge.Tests.Native;

[TestClass]
public sealed class HookSandboxTests
{
    /// <summary>
    /// Right after the tree is created, NTFS's copy of sub\deep's times in the index of "sub" still has deep's old write
    /// time. The real probe lists "sub" from that copy while the Agent reports deep's own time, so
    /// Probe_ThroughRelay_MatchesRealFolder and Probe_ThroughAgent_MatchesRealFolder flaked on CI. The sandbox must hand
    /// out a tree whose listed directory times already equal their own.
    /// </summary>
    [TestMethod]
    public void NewSandbox_ListedDirectoryTimes_MatchTheirOwn()
    {
        for (int i = 0; i < 5; i++)
        {
            using HookSandbox sandbox = new("x64");
            IReadOnlyList<string> unsettled = HookSandbox.FindUnsettledDirectories(sandbox.RealRoot);
            Assert.IsEmpty(unsettled, string.Join(Environment.NewLine, unsettled));
        }
    }
}
