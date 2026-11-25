using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Tests.Fakes;
using ProcBridge.Launcher.Wpf.ViewModels;

namespace ProcBridge.Launcher.Wpf.Tests;

/// <summary>Remote Explorer folder tree: lazy listing and refused listings shown to the user.</summary>
[TestClass]
public sealed class RemoteFolderTreeTests
{
    private const string Root = @"D:\src";

    private static FakeRemoteExplorer Explorer()
    {
        var explorer = new FakeRemoteExplorer { Drives = new DrivesResponse { Roots = [Root], Drives = [new DriveEntry { Name = @"D:\" }] } };
        explorer.Folders[Root] = [FakeRemoteExplorer.Dir(Root, "app"), FakeRemoteExplorer.File(Root, "readme.txt"), FakeRemoteExplorer.Dir(Root, "Lib")];
        explorer.Folders[Root + @"\app"] = [FakeRemoteExplorer.Dir(Root + @"\app", "bin")];
        return explorer;
    }

    [TestMethod]
    public async Task Load_ShowsTheAllowedRoots_WithoutListingThem()
    {
        FakeRemoteExplorer explorer = Explorer();
        var tree = new RemoteFolderTreeViewModel(explorer);

        await tree.LoadAsync();

        RemoteFolderNodeViewModel root = tree.Roots.Single();
        Assert.AreEqual(Root, root.FullPath);
        Assert.IsFalse(tree.HasError);
        // Only a placeholder until expanded: nothing listed yet.
        Assert.IsTrue(root.Children.Single().IsPlaceholder);
        CollectionAssert.AreEqual(new[] { "drives" }, explorer.Calls.ToArray());
    }

    [TestMethod]
    public async Task Expand_ListsOnce_FoldersInTheTree_FilesApart()
    {
        FakeRemoteExplorer explorer = Explorer();
        var tree = new RemoteFolderTreeViewModel(explorer);
        await tree.LoadAsync();
        RemoteFolderNodeViewModel root = tree.Roots.Single();

        root.IsExpanded = true;
        await root.EnsureLoadedAsync();
        root.IsExpanded = false;
        root.IsExpanded = true;
        await root.EnsureLoadedAsync();

        CollectionAssert.AreEqual(new[] { "app", "Lib" }, root.Children.Select(x => x.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "readme.txt" }, root.Files.Select(x => x.Name).ToArray());
        Assert.AreEqual(1, explorer.Calls.Count(x => x == $"list {Root}"));
        // Children are lazy as well.
        Assert.IsFalse(explorer.Calls.Contains($@"list {Root}\app"));
        RemoteFolderNodeViewModel app = root.Children[0];
        app.IsExpanded = true;
        await app.EnsureLoadedAsync();
        Assert.AreEqual("bin", app.Children.Single().Name);
    }

    [TestMethod]
    public async Task Select_UpdatesTheSelectedPath_AndListsTheFolder()
    {
        FakeRemoteExplorer explorer = Explorer();
        var tree = new RemoteFolderTreeViewModel(explorer);
        await tree.LoadAsync();

        tree.Roots[0].IsSelected = true;
        await tree.Roots[0].EnsureLoadedAsync();

        Assert.AreEqual(Root, tree.SelectedPath);
        Assert.HasCount(1, tree.Roots[0].Files);
    }

    [TestMethod]
    [DataRow(ExplorerStatus.PermissionDenied)]
    [DataRow(ExplorerStatus.OutOfScope)]
    public async Task RefusedFolder_ShowsWhy_AndHasNoChildren(ExplorerStatus status)
    {
        FakeRemoteExplorer explorer = Explorer();
        explorer.RefusedFolders[Root + @"\app"] = status;
        var tree = new RemoteFolderTreeViewModel(explorer);
        await tree.LoadAsync();
        RemoteFolderNodeViewModel root = tree.Roots[0];
        root.IsExpanded = true;
        await root.EnsureLoadedAsync();
        RemoteFolderNodeViewModel app = root.Children[0];

        app.IsExpanded = true;
        await app.EnsureLoadedAsync();

        Assert.IsTrue(app.HasError);
        StringAssert.StartsWith(app.ErrorText, ExplorerStatusText.Describe(status));
        Assert.IsEmpty(app.Children);
        Assert.IsFalse(app.IsLoaded);
    }

    [TestMethod]
    public async Task NoFilePermission_ShowsPermissionDenied_ForTheWholeTree()
    {
        var explorer = new FakeRemoteExplorer { Drives = new DrivesResponse { Status = ExplorerStatus.PermissionDenied } };
        var tree = new RemoteFolderTreeViewModel(explorer);

        await tree.LoadAsync();

        Assert.IsEmpty(tree.Roots);
        Assert.AreEqual(ExplorerStatusText.Describe(ExplorerStatus.PermissionDenied), tree.ErrorText);
    }

    [TestMethod]
    public async Task NoRoots_SaysMachineBGrantedNothing()
    {
        var tree = new RemoteFolderTreeViewModel(new FakeRemoteExplorer());

        await tree.LoadAsync();

        Assert.IsTrue(tree.HasError);
        StringAssert.Contains(tree.ErrorText, "chưa cấp");
    }

    [TestMethod]
    public void StatusText_CoversEveryStatus_AndTellsPermissionFromScope()
    {
        foreach (ExplorerStatus status in Enum.GetValues<ExplorerStatus>())
            Assert.IsFalse(string.IsNullOrWhiteSpace(ExplorerStatusText.Describe(status)), status.ToString());
        Assert.AreNotEqual(ExplorerStatusText.Describe(ExplorerStatus.PermissionDenied), ExplorerStatusText.Describe(ExplorerStatus.OutOfScope));
        StringAssert.Contains(ExplorerStatusText.Describe(ExplorerStatus.OutOfScope, "x"), "(x)");
    }
}
