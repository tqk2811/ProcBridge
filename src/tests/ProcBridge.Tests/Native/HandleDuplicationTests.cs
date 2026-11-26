using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// A virtual handle duplicated into another hooked process (DuplicateHandle with the child as target, the value passed
/// over a pipe) is tracked there too: the child sees the virtual name and, through the Broker, machine B's identity.
/// </summary>
[TestClass]
public sealed class HandleDuplicationTests
{
    [TestMethod]
    [DataRow("x64", "")]
    [DataRow("Win32", "")]
    [DataRow("x64", "close-source")]
    [DataRow("Win32", "close-source")]
    public void DuplicateIntoChild_LocalMapping_KeepsVirtualName(string platform, string mode)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{sandbox.Binaries.Probe}\" {mode}");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64", "")]
    [DataRow("Win32", "")]
    [DataRow("x64", "close-source")]
    [DataRow("Win32", "close-source")]
    public async Task DuplicateIntoChild_ThroughAgent_KeepsMachineBView(string platform, string mode)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{sandbox.Binaries.Probe}\" {mode}");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64", "write-close-source")]
    [DataRow("Win32", "write-close-source")]
    [DataRow("x64", "write")]
    [DataRow("Win32", "write")]
    public async Task ChildWritingDuplicatedHandleAfterParentClosed_LandsOnMachineB(string platform, string mode)
    {
        // The parent creates the file, duplicates its write handle into a child and closes its own (with
        // DUPLICATE_CLOSE_SOURCE or right after), then the child appends three times. Write-back must wait for the
        // child: everything it wrote reaches machine B, not only what the parent wrote.
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{sandbox.VirtualRoot}\\out.txt\" \"\" {mode}");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        // Checked before the session ends: the child's own close wrote it back, not the final sync of open files.
        Assert.AreEqual("parent+a+b+c", File.ReadAllText(Path.Combine(sandbox.RealRoot, "out.txt")));
        Assert.AreEqual(0, session.Broker.WriteBack.PendingCount);
    }

    [TestMethod]
    [DataRow("x64", "Win32")]
    [DataRow("Win32", "x64")]
    public void DuplicateIntoChildOfOtherArchitecture_KeepsVirtualName(string platform, string childPlatform)
    {
        using HookSandbox sandbox = new(platform);
        string file = sandbox.VirtualRoot + "\\a.txt";
        string childProbe = new NativeBinaries(childPlatform).Probe;

        ProcessResult local = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{childProbe}\"");

        AssertChildSeesParentView(file, local);
    }

    [TestMethod]
    [DataRow("x64", "Win32")]
    [DataRow("Win32", "x64")]
    public async Task DuplicateIntoChildOfOtherArchitecture_ThroughAgent_KeepsMachineBView(string platform, string childPlatform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string file = sandbox.VirtualRoot + "\\a.txt";
        string childProbe = new NativeBinaries(childPlatform).Probe;

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{file}\" \"{childProbe}\"");

        AssertChildSeesParentView(file, result);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void HookedProcess_HasNoHandleInbox(string platform)
    {
        // The shared memory inbox (Local\ProcBridge-HandleInbox-<pid>) is gone: lazy adopt finds duplicated handles, so
        // no name is created that another process could take first or write fake entries into.
        using HookSandbox sandbox = new(platform);

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --inbox-section");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("inbox: absent", result.StandardOutput.Trim());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ChildPullingHandleFromParent_ThroughAgent_SeesVirtualNameAndWritesBack(string platform)
    {
        // Pull direction (HandleSharing-Design-vi.md 10, T5): the child calls DuplicateHandle with the parent as source, so
        // no hook ever sends anything. The child's hook checks the new handle right away, Adopt makes it a holder of the
        // parent's write token, and what it writes after the parent closed its own handle reaches machine B.
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);
        string file = sandbox.VirtualRoot + "\\out.txt";

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --pull-test \"{file}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual($"child: \\\\?\\{file}", result.StandardOutput.Trim());
        // Checked before the session ends: the child's close wrote it back, not the final sync of open files.
        Assert.AreEqual("parent+a+b+c", File.ReadAllText(Path.Combine(sandbox.RealRoot, "out.txt")));
        Assert.AreEqual(0, session.Broker.WriteBack.PendingCount);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void HandlePushedByProcessWithoutHook_IsRecognisedByHookedReceiver(string platform)
    {
        // T7: a process without a hook (run directly, not under Spawn) holds a handle to the backing file and duplicates it
        // into a hooked process; nothing announces it, the receiver's first lookup finds the real name under the root.
        using HookSandbox sandbox = new(platform);
        string realFile = Path.Combine(sandbox.RealRoot, "a.txt");

        ProcessResult result = sandbox.Run(sandbox.Binaries.Probe,
            $"--outside-push \"{realFile}\" \"{sandbox.Binaries.Spawn}\" \"{sandbox.VirtualRoot}\" \"{sandbox.RealRoot}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.StartsWith($"child: \\\\?\\{sandbox.VirtualRoot}\\a.txt id ", result.StandardOutput.Trim());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void VirtualHandleOnValueOfClosedRealHandle_IsRecognised(string platform)
    {
        // T8: the child names a file outside the root (its handle value goes into the negative cache) and closes it; the
        // parent then duplicates a virtual handle in, which gets the same value and the same access: the close must have
        // made the hook forget what it knew about that value.
        using HookSandbox sandbox = new(platform);
        string outside = Path.Combine(sandbox.BaseDirectory, "outside.txt");
        File.WriteAllText(outside, "outside");
        string file = sandbox.VirtualRoot + "\\a.txt";

        ProcessResult result = sandbox.RunHooked($"\"{sandbox.Binaries.Probe}\" --dup-reuse \"{file}\" \"{outside}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual($"child: reused \\\\?\\{file}", result.StandardOutput.Trim());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public void RealHandleUsedManyTimes_IsNamedOnce(string platform)
    {
        // T9: a real handle the hook never saw opened (pushed in by a process without a hook) is named once at its first
        // lookup; the later lookups hit the negative cache.
        using HookSandbox sandbox = new(platform);
        string outside = Path.Combine(sandbox.BaseDirectory, "outside.txt");
        File.WriteAllText(outside, "outside");

        ProcessResult result = sandbox.Run(sandbox.Binaries.Probe,
            $"--outside-push \"{outside}\" \"{sandbox.Binaries.Spawn}\" \"{sandbox.VirtualRoot}\" \"{sandbox.RealRoot}\" count");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("name queries: 1", result.StandardOutput.Trim());
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task TwoThreadsMeetingPushedHandleAtOnce_CloseWritesBack(string platform)
    {
        // T10: two threads of the child use the pushed handle for the first time together. One entry must hold the token,
        // so the child's single close ends its share and the file is written back right then (a second entry would keep
        // the token counted in the child until it exits; the child waits for machine B before exiting).
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox, writable: true);
        string machineBFile = Path.Combine(sandbox.RealRoot, "out.txt");

        ProcessResult result = session.Run(
            $"\"{sandbox.Binaries.Probe}\" --dup-to-child \"{sandbox.VirtualRoot}\\out.txt\" \"\" write-threads \"{machineBFile}\"");

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("child: landed", result.StandardOutput.Trim());
        Assert.AreEqual("parent+t+t", File.ReadAllText(machineBFile));
    }

    private static void AssertChildSeesParentView(string file, ProcessResult result)
    {
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, lines, result.StandardOutput);
        Assert.StartsWith($"parent: \\\\?\\{file} id ", lines[0]);
        // Same name, file id and write time as the parent's handle (with the Broker: machine B's, not the cache copy's).
        Assert.AreEqual("child:" + lines[0]["parent:".Length..], lines[1]);
    }
}
