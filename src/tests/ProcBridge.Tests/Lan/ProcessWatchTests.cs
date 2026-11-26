using System.Collections.Concurrent;
using System.Diagnostics;
using ProcBridge.Broker;
using ProcBridge.Broker.Enums;
using ProcBridge.Broker.Models;

namespace ProcBridge.Tests.Lan;

/// <summary>The Broker learns when a process holding write tokens exits (HandleSharing-Design-vi.md section 5.4).</summary>
[TestClass]
public sealed class ProcessWatchTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A child that runs until its stdin closes (or it is killed).</summary>
    private static Process StartChild()
        => Process.Start(new ProcessStartInfo("cmd.exe", "/d /q /k")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private sealed class Exits
    {
        private readonly ConcurrentQueue<ProcessKey> _seen = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task OnExited(ProcessKey key)
        {
            _seen.Enqueue(key);
            _signal.Release();
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<ProcessKey> Seen => _seen.ToArray();

        public async Task<ProcessKey> NextAsync()
        {
            Assert.IsTrue(await _signal.WaitAsync(Wait), "No exit was reported.");
            return _seen.Last();
        }
    }

    [TestMethod]
    public async Task ChildThatExits_IsReportedOnce()
    {
        var exits = new Exits();
        await using var watch = new ProcessWatch(exits.OnExited);
        using Process child = StartChild();
        ProcessKey key = ProcessWatch.TryGetKey(child.Id) ?? throw new AssertFailedException("cannot open the child");

        Assert.AreEqual(ProcessWatchResult.Watching, watch.Watch(key));
        Assert.AreEqual(ProcessWatchResult.Watching, watch.Watch(key), "watching again is a no-op");
        Assert.AreEqual(1, watch.Count);
        Assert.IsEmpty(exits.Seen);

        child.StandardInput.Close();
        Assert.AreEqual(key, await exits.NextAsync());
        await Task.Delay(200);
        Assert.HasCount(1, exits.Seen);
        Assert.AreEqual(0, watch.Count);
    }

    [TestMethod]
    public async Task KilledChild_IsReported()
    {
        var exits = new Exits();
        await using var watch = new ProcessWatch(exits.OnExited);
        using Process child = StartChild();
        ProcessKey key = ProcessWatch.TryGetKey(child.Id)!.Value;
        watch.Watch(key);

        child.Kill();

        Assert.AreEqual(key, await exits.NextAsync());
    }

    [TestMethod]
    public async Task ProcessAlreadyGone_IsReportedAtOnce()
    {
        var exits = new Exits();
        await using var watch = new ProcessWatch(exits.OnExited);
        using Process child = StartChild();
        ProcessKey key = ProcessWatch.TryGetKey(child.Id)!.Value;
        child.Kill();
        await child.WaitForExitAsync();
        child.Dispose(); // the last handle: the id may be reused from now on

        ProcessWatchResult result = watch.Watch(key);

        Assert.AreEqual(ProcessWatchResult.Exited, result);
        Assert.AreEqual(key, await exits.NextAsync());
        Assert.AreEqual(0, watch.Count);
    }

    [TestMethod]
    public async Task ReusedProcessId_IsNotTakenForTheOldProcess()
    {
        // The id is alive, but with another creation time: the process of the key is gone.
        var exits = new Exits();
        await using var watch = new ProcessWatch(exits.OnExited);
        ProcessKey self = ProcessWatch.TryGetKey(Environment.ProcessId)!.Value;
        ProcessKey older = self with { CreationTime = self.CreationTime - 1 };

        Assert.AreEqual(ProcessWatchResult.Exited, watch.Watch(older));
        Assert.AreEqual(older, await exits.NextAsync());
        Assert.AreEqual(ProcessWatchResult.Watching, watch.Watch(self), "the live process itself is watched");
    }

    [TestMethod]
    public async Task ExitedWithCodeStillActive_IsReportedExited()
    {
        // Exit code 259 is STILL_ACTIVE: only the signaled handle tells the process is gone. The Process object keeps
        // the handle open, so the id still names the exited process.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c exit 259") { UseShellExecute = false, CreateNoWindow = true })!;
        await child.WaitForExitAsync();
        Assert.AreEqual(259, child.ExitCode);
        ProcessKey key = ProcessWatch.TryGetKey(child.Id)!.Value;
        var exits = new Exits();
        await using var watch = new ProcessWatch(exits.OnExited);

        Assert.AreEqual(ProcessWatchResult.Exited, watch.Watch(key));
        Assert.AreEqual(key, await exits.NextAsync());
    }

    [TestMethod]
    public async Task KeyWithoutCreationTime_IsNotWatched()
    {
        var exits = new Exits();
        await using var watch = new ProcessWatch(exits.OnExited);

        Assert.AreEqual(ProcessWatchResult.Unavailable, watch.Watch(new ProcessKey(Environment.ProcessId, 0)));
        Assert.AreEqual(ProcessWatchResult.Unavailable, watch.Watch(new ProcessKey(-3, 0)));
        Assert.AreEqual(0, watch.Count);
    }

    [TestMethod]
    public void TryGetKey_OfCurrentProcess_HasItsCreationTime()
    {
        ProcessKey? key = ProcessWatch.TryGetKey(Environment.ProcessId);

        Assert.IsNotNull(key);
        long started = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc();
        Assert.IsLessThan(TimeSpan.FromSeconds(2).Ticks, Math.Abs(started - key.Value.CreationTime));
    }
}
