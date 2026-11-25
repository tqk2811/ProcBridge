using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Remote Explorer tab: pages follow the profile machine B granted; writes need their permission and a yes.</summary>
[TestClass]
public sealed class RemoteExplorerTests
{
    private sealed class Factory(FakeRemoteExplorer explorer) : IRemoteExplorerFactory
    {
        public IRemoteExplorer Create(PairedMachine machine) => explorer;
    }

    private static async Task<(RemoteExplorerViewModel Vm, FakeRemoteExplorer Explorer, FakeDialogs Dialogs)> ConnectAsync(ExplorerPermissions permissions)
    {
        var explorer = new FakeRemoteExplorer
        {
            Policy = new ExplorerPolicyResponse { Permissions = permissions, RegistryRoots = [@"HKCU\Software\Vendor"] },
            Processes = new ProcessListResponse { Processes = [new ProcessEntry { Pid = 42, Name = "app.exe" }] },
        };
        var registry = new FakeRegistry();
        registry.Machines.Add(FakeRegistry.Machine("B", "FP"));
        var dialogs = new FakeDialogs();
        var vm = new RemoteExplorerViewModel(new MachinesViewModel(registry, dialogs), new Factory(explorer), dialogs);
        vm.SelectedMachine = vm.Machines.Machines[0];
        await vm.ConnectAsync();
        return (vm, explorer, dialogs);
    }

    [TestMethod]
    public async Task ReadOnlyProfile_HidesEveryWrite_AndSkipsPagesNotGranted()
    {
        (RemoteExplorerViewModel vm, FakeRemoteExplorer explorer, _) = await ConnectAsync(ExplorerPermissions.ProcessRead | ExplorerPermissions.RegistryRead);

        Assert.IsTrue(vm.IsConnected);
        Assert.IsFalse(vm.CanWriteFiles);
        Assert.IsFalse(vm.Processes!.CanKill);
        Assert.IsFalse(vm.Registry!.CanWrite);
        Assert.IsFalse(vm.Services!.CanControl);
        Assert.HasCount(1, vm.Processes.Processes);
        Assert.AreEqual(@"HKCU\Software\Vendor", vm.Registry.Path);
        // Not granted: no call, a clear message instead.
        Assert.IsFalse(explorer.Calls.Contains("services"));
        Assert.IsFalse(explorer.Calls.Contains("drives"));
        StringAssert.Contains(vm.Services.ErrorText, nameof(ExplorerPermissions.ServiceRead));
        StringAssert.Contains(vm.SystemInfo!.ErrorText, nameof(ExplorerPermissions.SystemInfo));
        vm.Processes.SelectedProcess = vm.Processes.Processes[0];
        Assert.IsFalse(vm.Processes.KillCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task Kill_AsksFirst_AndOnlyCallsAfterYes()
    {
        (RemoteExplorerViewModel vm, FakeRemoteExplorer explorer, FakeDialogs dialogs) = await ConnectAsync(ExplorerPermissions.ProcessRead | ExplorerPermissions.ProcessKill);
        ProcessesViewModel processes = vm.Processes!;
        processes.SelectedProcess = processes.Processes[0];
        Assert.IsTrue(processes.CanKill);

        dialogs.ConfirmAnswer = false;
        await processes.KillCommand.ExecuteAsync();
        Assert.IsFalse(explorer.Calls.Any(x => x.StartsWith("kill")));

        dialogs.ConfirmAnswer = true;
        await processes.KillCommand.ExecuteAsync();
        Assert.IsTrue(explorer.Calls.Contains("kill 42 tree=True"));
        Assert.HasCount(2, dialogs.Questions);
        StringAssert.Contains(dialogs.Questions[0], "42");
    }

    [TestMethod]
    public async Task RefusedWrite_ShowsTheReason()
    {
        (RemoteExplorerViewModel vm, FakeRemoteExplorer explorer, FakeDialogs dialogs) = await ConnectAsync(ExplorerPermissions.ServiceRead | ExplorerPermissions.ServiceControl);
        explorer.WriteStatus = ExplorerStatus.AccessDenied;
        dialogs.ConfirmAnswer = true;
        ServicesViewModel services = vm.Services!;
        services.SelectedService = new ServiceEntry { Name = "svc" };

        await services.ControlCommand.ExecuteAsync(ServiceControlAction.Stop);

        Assert.IsTrue(explorer.Calls.Contains("service svc Stop"));
        StringAssert.Contains(services.StatusText, "Windows");
    }

    [TestMethod]
    public async Task NoPermissionAtAll_SaysSo_AndDisconnectDisposes()
    {
        (RemoteExplorerViewModel vm, FakeRemoteExplorer explorer, _) = await ConnectAsync(ExplorerPermissions.None);
        StringAssert.Contains(vm.StatusText, "chưa cấp quyền nào");

        await vm.DisconnectAsync();

        Assert.IsTrue(explorer.Disposed);
        Assert.IsNull(vm.Processes);
        Assert.IsFalse(vm.IsConnected);
    }
}
