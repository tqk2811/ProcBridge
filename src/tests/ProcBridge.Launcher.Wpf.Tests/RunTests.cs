using ProcBridge.Launcher.Enums;
using ProcBridge.Launcher.Wpf.Enums;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Services;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Run/Stop: the runner (LaunchSession in production) gets the profile's options; Stop cancels the tree.</summary>
[TestClass]
public sealed class RunTests
{
    private static readonly PairedMachine MachineB = FakeRegistry.Machine("B", "FING-ERPR-INTB", "host-b:6000");

    private static readonly LaunchProfile Profile = new()
    {
        Name = "p",
        MachineFingerprint = MachineB.Fingerprint,
        ShareName = "src",
        ShareRoot = @"D:\src",
        Mappings = [new FolderMapping { VirtualRoot = @"V:\p", RemotePath = @"D:\src\p" }],
        Program = "cmd.exe",
        Arguments = "/c dir",
        DisableBuildServers = false,
    };

    [TestMethod]
    public async Task Run_PassesTheProfileOptions_AndShowsOutputAndExitCode()
    {
        var runner = new FakeRunner { Lines = ["one", "two"], ExitCode = 3 };
        var run = new RunViewModel(runner, new LaunchOptionsFactory(@"C:\pb"));

        await run.RunAsync(Profile, MachineB);

        Assert.AreEqual("host-b", runner.Options!.AgentHost);
        Assert.AreEqual(6000, runner.Options.AgentPort);
        Assert.AreEqual("p", runner.Options.Mappings.Single().RemotePath);
        Assert.AreEqual("cmd.exe /c dir", runner.Options.CommandLine);
        Assert.IsFalse(runner.Options.DisableBuildServers);
        CollectionAssert.AreEqual(new[] { "> cmd.exe /c dir", "one", "two" }, run.Log.ToArray());
        Assert.AreEqual(RunState.Exited, run.State);
        StringAssert.Contains(run.StatusText, "3");
    }

    [TestMethod]
    public async Task Stop_CancelsTheRun()
    {
        var runner = new FakeRunner { WaitForCancel = true };
        var run = new RunViewModel(runner, new LaunchOptionsFactory(@"C:\pb"));

        Task running = run.RunAsync(Profile, MachineB);
        await runner.Started.Task;
        Assert.IsTrue(run.IsRunning);
        Assert.IsTrue(run.StopCommand.CanExecute(null));
        run.StopCommand.Execute(null);
        await running;

        Assert.IsTrue(runner.WasCancelled);
        Assert.AreEqual(RunState.Stopped, run.State);
        Assert.IsFalse(run.IsRunning);
    }

    [TestMethod]
    public async Task InvalidProfile_DoesNotCallTheRunner()
    {
        var runner = new FakeRunner();
        var run = new RunViewModel(runner, new LaunchOptionsFactory(@"C:\pb"));

        await run.RunAsync(Profile with { Mappings = [] }, MachineB);

        Assert.IsNull(runner.Options);
        Assert.AreEqual(RunState.Failed, run.State);
        StringAssert.Contains(run.StatusText, "ánh xạ");
    }

    [TestMethod]
    public async Task ProfilesRunCommand_RunsTheSelectedProfile_OnItsMachine()
    {
        var registry = new FakeRegistry();
        registry.Machines.Add(MachineB);
        var store = new MemoryProfileStore();
        store.Saved.Add(Profile);
        var dialogs = new FakeDialogs();
        var runner = new FakeRunner();
        var profiles = new ProfilesViewModel(store, dialogs, new MachinesViewModel(registry, dialogs), new RunViewModel(runner, new LaunchOptionsFactory(@"C:\pb")));

        await profiles.RunCommand.ExecuteAsync();

        Assert.AreEqual(MachineB.Fingerprint, runner.Options!.AgentFingerprint);
        Assert.AreEqual(RunState.Exited, profiles.Run.State);
    }

    [TestMethod]
    [DataRow(LaunchFailureReason.AgentUnreachable, "Không kết nối được máy B")]
    [DataRow(LaunchFailureReason.ShareUnknown, "không có chia sẻ")]
    [DataRow(LaunchFailureReason.WorkingDirectoryMissing, "Thư mục làm việc")]
    public async Task Run_StoppedBeforeTheTarget_ShowsTheReasonInVietnamese(LaunchFailureReason reason, string expected)
    {
        var runner = new FakeRunner { Failure = new LaunchFailedException(reason, "target", "refused", "english text") };
        var run = new RunViewModel(runner, new LaunchOptionsFactory(@"C:\pb"));

        await run.RunAsync(Profile, MachineB);

        Assert.AreEqual(RunState.Failed, run.State);
        StringAssert.Contains(run.StatusText, expected);
        StringAssert.Contains(run.StatusText, "target");
        Assert.DoesNotContain("english text", run.StatusText);
    }
}
