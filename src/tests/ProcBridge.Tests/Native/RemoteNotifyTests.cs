using System.Text;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M7 acceptance (plan section 13): a process watching a virtual folder (NtNotifyChangeDirectoryFile[Ex]) sees the files
/// that change on machine B, through the Agent on loopback and the Broker.
/// </summary>
[TestClass]
public sealed class RemoteNotifyTests
{
    private static string Lines(ProcessResult result) => result.StandardOutput + result.StandardError;

    /// <summary>The Broker's change watcher reports a rescan when it connects: watches made before then would overflow.</summary>
    private static async Task WaitForChangeWatcherAsync(RemoteSession session)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!session.Broker.Cache.IsWatchActive)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The Broker's change watcher did not connect.");
            await Task.Delay(50);
        }
        // The rescan of the connection is dispatched to the watches asynchronously: wait until it has been applied.
        while (session.Broker.Pipe.Handler.Watches.DispatchedCount < 1)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The Broker did not dispatch the change watcher's rescan.");
            await Task.Delay(10);
        }
    }

    /// <summary>The hook removes a watch after the handle's close, on the thread pool: give it a moment.</summary>
    private static async Task WaitForNoWatchesAsync(RemoteSession session)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Broker.Pipe.Handler.Watches.Count != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(0, session.Broker.Pipe.Handler.Watches.Count);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task NotifyScenario_ThroughAgent_MatchesRealFolder(string platform)
    {
        using HookSandbox sandbox = new(platform);
        await using RemoteSession session = RemoteSession.Start(sandbox);
        await WaitForChangeWatcherAsync(session);
        string reference = Path.Combine(sandbox.BaseDirectory, "reference");
        Directory.CreateDirectory(Path.Combine(reference, "sub"));

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"--notify \"{reference}\" \"{reference}\"");
        // Watches the virtual folder while the files are made in machine B's folder behind it.
        ProcessResult remote = session.Run($"\"{sandbox.Binaries.Probe}\" --notify \"{sandbox.VirtualRoot}\" \"{sandbox.RealRoot}\"");

        Assert.AreEqual(0, real.ExitCode, Lines(real));
        Assert.AreEqual(0, remote.ExitCode, Lines(remote));
        Assert.Contains("notify.iocp: ok=1 key=0x1234 overlapped=1 action=1 name=n-iocp.txt", real.StandardOutput);
        Assert.Contains("notify.close: ended=1 signalled=1 status=0x10B", real.StandardOutput);
        Assert.Contains("notify.eventClosed: signalled=1 status=0x0", real.StandardOutput);
        HookComparisonTests.AssertSameLines(real.StandardOutput, remote.StandardOutput);
        // Every handle was closed: the hook removed its watches.
        await WaitForNoWatchesAsync(session);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("Win32")]
    public async Task ManyWatchedHandles_ShareOnePipeInstance_AndMatchRealFolder(string platform)
    {
        const int count = 64;
        using HookSandbox sandbox = new(platform);
        string reference = Path.Combine(sandbox.BaseDirectory, "reference");
        for (int i = 0; i < count; i++)
        {
            Directory.CreateDirectory(Path.Combine(reference, "m" + i));
            Directory.CreateDirectory(Path.Combine(sandbox.RealRoot, "m" + i));
        }
        await using RemoteSession session = RemoteSession.Start(sandbox);
        await WaitForChangeWatcherAsync(session);

        ProcessResult real = sandbox.Run(sandbox.Binaries.Probe, $"--notify-many \"{reference}\" \"{reference}\" {count}");
        ProcessResult remote = session.Run($"\"{sandbox.Binaries.Probe}\" --notify-many \"{sandbox.VirtualRoot}\" \"{sandbox.RealRoot}\" {count}");

        Assert.AreEqual(0, real.ExitCode, Lines(real));
        Assert.AreEqual(0, remote.ExitCode, Lines(remote));
        for (int round = 1; round <= 3; round++)
            Assert.Contains($"notify.many.round{round}: issued={count} seen={count}", real.StandardOutput);
        HookComparisonTests.AssertSameLines(real.StandardOutput, remote.StandardOutput);
        // One long poll serves every handle: far fewer pipe instances than watched handles.
        Assert.IsLessThan(16, session.Broker.Pipe.PeakClientCount, $"peak pipe connections {session.Broker.Pipe.PeakClientCount}");
        await WaitForNoWatchesAsync(session);
    }

    [TestMethod]
    public async Task FileSystemWatcher_SeesChangesOnMachineB()
    {
        using HookSandbox sandbox = new("x64");
        await using RemoteSession session = RemoteSession.Start(sandbox);
        await WaitForChangeWatcherAsync(session);
        string v = sandbox.VirtualRoot;
        string r = sandbox.RealRoot;
        // .NET Framework's FileSystemWatcher: overlapped ReadDirectoryChangesW completed through the thread pool's
        // completion port. The files change in machine B's folder, not through the virtual one.
        string script = $$"""
            $w = New-Object IO.FileSystemWatcher '{{v}}'
            $w.IncludeSubdirectories = $true
            foreach ($n in 'Created', 'Changed', 'Deleted', 'Renamed') { $null = Register-ObjectEvent $w $n -SourceIdentifier $n }
            $seen = @{}
            # Collects events until the wanted one is seen (or 30 s pass): each step waits for its own event.
            function Wait-Seen($key) {
                $deadline = (Get-Date).AddSeconds(30)
                while ((Get-Date) -lt $deadline -and -not $seen.ContainsKey($key)) {
                    $e = Wait-Event -Timeout 1
                    if ($e) {
                        $a = $e.SourceEventArgs
                        $line = '' + $a.ChangeType + ':' + $a.Name
                        if ($a -is [IO.RenamedEventArgs]) { $line += '<-' + $a.OldName }
                        $seen[$line] = 1
                        Remove-Event -EventIdentifier $e.EventIdentifier
                    }
                }
            }
            # EnableRaisingEvents issues the first read synchronously: the watch exists from here on.
            $w.EnableRaisingEvents = $true
            [IO.File]::WriteAllText('{{r}}\fsw.txt', 'one')
            Wait-Seen 'Created:fsw.txt'
            [IO.File]::AppendAllText('{{r}}\fsw.txt', 'two')
            Wait-Seen 'Changed:fsw.txt'
            [IO.File]::Move('{{r}}\fsw.txt', '{{r}}\fsw2.txt')
            Wait-Seen 'Renamed:fsw2.txt<-fsw.txt'
            [IO.File]::Delete('{{r}}\fsw2.txt')
            Wait-Seen 'Deleted:fsw2.txt'
            $w.Dispose()
            ($seen.Keys | Sort-Object) -join "`n"
            """;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        ProcessResult result = session.Run($"powershell.exe -NoProfile -NonInteractive -EncodedCommand {encoded}");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        string[] seen = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CollectionAssert.Contains(seen, "Created:fsw.txt", Lines(result));
        CollectionAssert.Contains(seen, "Changed:fsw.txt", Lines(result));
        CollectionAssert.Contains(seen, "Renamed:fsw2.txt<-fsw.txt", Lines(result));
        CollectionAssert.Contains(seen, "Deleted:fsw2.txt", Lines(result));
    }

    [TestMethod]
    public async Task ReadDirectoryChanges_OnRealFolder_GoesToTheSystem()
    {
        // A folder outside the virtual one is watched by the file system itself while the Broker session is active.
        using HookSandbox sandbox = new("x64");
        await using RemoteSession session = RemoteSession.Start(sandbox);
        string outside = Path.Combine(sandbox.BaseDirectory, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "sub"));

        ProcessResult result = session.Run($"\"{sandbox.Binaries.Probe}\" --notify \"{outside}\" \"{outside}\"");

        Assert.AreEqual(0, result.ExitCode, Lines(result));
        Assert.Contains("notify.event: ok=1 action=1 name=n-event.txt", result.StandardOutput);
        Assert.Contains("notify.close: ended=1 signalled=1 status=0x10B", result.StandardOutput);
        await WaitForNoWatchesAsync(session);
    }
}
