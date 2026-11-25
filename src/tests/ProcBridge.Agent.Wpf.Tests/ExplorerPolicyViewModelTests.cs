using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Wpf.Core;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Tests.Fakes;

namespace ProcBridge.Agent.Wpf.Tests;

[TestClass]
public sealed class ExplorerPolicyViewModelTests
{
    private const string Fingerprint = "AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA";

    [TestMethod]
    public void MachineWithoutProfile_StartsWithEverythingOff()
    {
        var viewModel = new ExplorerPolicyViewModel(Fingerprint, "a", new ExplorerPolicyStore(), new FakeDialogService());

        Assert.IsTrue(viewModel.Permissions.All(x => !x.IsChecked));
        Assert.IsEmpty(viewModel.FileRoots);
        Assert.IsEmpty(viewModel.RegistryRoots);
        Assert.AreEqual(ExplorerPermissions.None, viewModel.BuildPolicy().Permissions);
    }

    [TestMethod]
    public void Save_StoresRootsAndCheckedFlags()
    {
        var store = new ExplorerPolicyStore();
        var viewModel = new ExplorerPolicyViewModel(Fingerprint, "a", store, new FakeDialogService());
        bool? closedWith = null;
        viewModel.CloseRequested += x => closedWith = x;

        Assert.IsTrue(viewModel.AddFileRoot(@"C:\Data\"));
        Assert.IsTrue(viewModel.AddFileRoot(@"D:\"));
        Assert.IsTrue(viewModel.AddRegistryRoot(@"HKCU\Software\Vendor\"));
        viewModel.Permissions.Single(x => x.Flag == ExplorerPermissions.FileRead).IsChecked = true;
        viewModel.Permissions.Single(x => x.Flag == ExplorerPermissions.RegistryRead).IsChecked = true;
        viewModel.SaveCommand.Execute(null);

        ExplorerPolicy saved = store.Get(Fingerprint);
        CollectionAssert.AreEqual(new[] { @"C:\Data", @"D:\" }, saved.FileRoots.ToArray());
        CollectionAssert.AreEqual(new[] { @"HKCU\Software\Vendor" }, saved.RegistryRoots.ToArray());
        Assert.AreEqual(ExplorerPermissions.FileRead | ExplorerPermissions.RegistryRead, saved.Permissions);
        Assert.IsTrue(closedWith);
    }

    [TestMethod]
    public void ExistingProfile_IsLoaded_AndCancelLeavesItUnchanged()
    {
        var store = new ExplorerPolicyStore();
        store.Set(Fingerprint, new ExplorerPolicy { FileRoots = [@"C:\Data"], Permissions = ExplorerPermissions.ProcessRead | ExplorerPermissions.ProcessKill });
        var viewModel = new ExplorerPolicyViewModel(Fingerprint, "a", store, new FakeDialogService());

        Assert.IsTrue(viewModel.Permissions.Single(x => x.Flag == ExplorerPermissions.ProcessKill).IsChecked);
        Assert.IsFalse(viewModel.Permissions.Single(x => x.Flag == ExplorerPermissions.FileWrite).IsChecked);
        viewModel.Permissions.Single(x => x.Flag == ExplorerPermissions.ProcessKill).IsChecked = false;
        viewModel.CancelCommand.Execute(null);

        Assert.AreEqual(ExplorerPermissions.ProcessRead | ExplorerPermissions.ProcessKill, store.Get(Fingerprint).Permissions);
    }

    [TestMethod]
    [DataRow("Software\\Vendor")]
    [DataRow("HKXX\\Software")]
    [DataRow("   ")]
    public void AddRegistryRoot_RejectsKeysWithoutAKnownHive(string key)
    {
        var viewModel = new ExplorerPolicyViewModel(Fingerprint, "a", new ExplorerPolicyStore(), new FakeDialogService());

        Assert.IsFalse(viewModel.AddRegistryRoot(key));
        Assert.IsEmpty(viewModel.RegistryRoots);
        Assert.IsNotNull(viewModel.ErrorText);
    }

    [TestMethod]
    public void AddFileRoot_RejectsRelativePaths_AndPickerAddsTheFolder()
    {
        var dialogs = new FakeDialogService { FolderToPick = @"E:\Builds" };
        var viewModel = new ExplorerPolicyViewModel(Fingerprint, "a", new ExplorerPolicyStore(), dialogs);

        Assert.IsFalse(viewModel.AddFileRoot(@"relative\folder"));
        viewModel.AddFileRootCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { @"E:\Builds" }, viewModel.FileRoots.ToArray());
    }

    [TestMethod]
    public void EveryPermissionFlag_HasALabel_AndAnEditorCheckBox()
    {
        foreach (ExplorerPermissions flag in Enum.GetValues<ExplorerPermissions>().Where(x => x != ExplorerPermissions.None))
            Assert.IsFalse(string.IsNullOrWhiteSpace(ExplorerPermissionLabels.Describe(flag)), flag.ToString());
        var viewModel = new ExplorerPolicyViewModel(Fingerprint, "a", new ExplorerPolicyStore(), new FakeDialogService());
        Assert.HasCount(Enum.GetValues<ExplorerPermissions>().Length - 1, viewModel.Permissions);
    }
}
