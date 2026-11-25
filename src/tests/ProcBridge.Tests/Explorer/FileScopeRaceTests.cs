using System.Diagnostics;
using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

/// <summary>
/// Check-then-use races (TOCTOU): right after a path passed the scope check, a folder on it is
/// swapped for a junction leading outside the root. The operation must still act on what was
/// checked, never on the junction target.
/// </summary>
[TestClass]
public sealed class FileScopeRaceTests
{
    private const ExplorerPermissions ReadWrite = ExplorerPermissions.FileRead | ExplorerPermissions.FileWrite;

    private string _base = null!;
    private string _root = null!;
    private string _outside = null!;
    private string _folder = null!;

    [TestInitialize]
    public void Setup()
    {
        _base = Path.Combine(Path.GetTempPath(), "ProcBridgeRace_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        _folder = Path.Combine(_root, "d");
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_folder, "f.txt"), "inside");
        File.WriteAllText(Path.Combine(_outside, "f.txt"), "top-secret");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories)
                     .Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)).ToList())
            Directory.Delete(dir, false);
        Directory.Delete(_base, true);
    }

    private bool _swapped;
    private IOException? _swapFailure;
    private string _swapTarget = null!;

    /// <summary>Session whose guard swaps <see cref="_folder"/> for a junction to the outside folder after the check of <paramref name="trigger"/>.</summary>
    private FileExplorerProvider SwappingFiles(string trigger) => SwappingFiles(trigger, _folder, _outside);

    /// <summary>Session whose guard moves <paramref name="victim"/> away and puts a junction to <paramref name="destination"/> in its place after the check of <paramref name="trigger"/>.</summary>
    private FileExplorerProvider SwappingFiles(string trigger, string victim, string destination)
    {
        _swapTarget = victim;
        var files = new ExplorerSession(new ExplorerPolicy { FileRoots = [_root], Permissions = ReadWrite }, "machine-a").Files;
        files.Guard.AfterScopeCheck = path =>
        {
            if (!string.Equals(path, trigger, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                Directory.Move(victim, victim + ".old");
            }
            catch (IOException ex)
            {
                _swapFailure = ex; // the checked handle pins the folder: the swap is impossible
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                _swapFailure = new IOException(ex.Message, ex);
                return;
            }

            CreateJunction(victim, destination);
            _swapped = true;
        };
        return files;
    }

    /// <summary>
    /// A test that never managed to swap proves nothing, so the only accepted reason for a failed swap
    /// is the checked handle holding the folder (sharing violation or access denied while it is open).
    /// </summary>
    private void AssertSwapOutcome()
    {
        if (_swapped)
        {
            Assert.IsTrue(File.GetAttributes(_swapTarget).HasFlag(FileAttributes.ReparsePoint), "swapped folder is not a junction");
            return;
        }

        Assert.IsNotNull(_swapFailure, "the swap hook never ran");
        var code = _swapFailure.InnerException is UnauthorizedAccessException ? 5 : _swapFailure.HResult & 0xFFFF;
        Assert.IsTrue(code is 32 or 5, $"swap failed for an unexpected reason: {_swapFailure.Message} (code {code})");
    }

    [TestMethod]
    public void ListDirectory_FolderSwappedAfterCheck_ListsCheckedFolder()
    {
        File.WriteAllText(Path.Combine(_folder, "only-inside.txt"), "x");

        var response = SwappingFiles(_folder).ListDirectory(new DirectoryListRequest { Path = _folder });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        CollectionAssert.Contains(response.Entries.Select(e => e.Name).ToArray(), "only-inside.txt");
        AssertSwapOutcome();
    }

    [TestMethod]
    public void ReadFile_ParentSwappedAfterCheck_NeverReadsOutside()
    {
        var path = Path.Combine(_folder, "f.txt");

        var response = SwappingFiles(path).ReadFile(new FileReadRequest { Path = path, Count = 100 });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual("inside", System.Text.Encoding.ASCII.GetString(response.Data));
        AssertSwapOutcome();
    }

    [TestMethod]
    public void Stat_ParentSwappedAfterCheck_ReportsCheckedFile()
    {
        var path = Path.Combine(_folder, "f.txt");

        var response = SwappingFiles(path).Stat(new FileStatRequest { Path = path });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual(6, response.Entry!.Size);
        Assert.AreEqual("f.txt", response.Entry.Name);
        AssertSwapOutcome();
    }

    [TestMethod]
    public void Delete_ParentSwappedAfterCheck_NeverDeletesOutside()
    {
        var path = Path.Combine(_folder, "f.txt");

        var response = SwappingFiles(path).Delete(new FileDeleteRequest { Path = path });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_outside, "f.txt")));
        AssertSwapOutcome();
    }

    [TestMethod]
    public void DeleteRecursive_FolderSwappedAfterCheck_NeverDeletesOutside()
    {
        var path = Path.Combine(_folder, "inner");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(_outside, "inner"));
        File.WriteAllText(Path.Combine(_outside, "inner", "keep.txt"), "keep");

        var response = SwappingFiles(path).Delete(new FileDeleteRequest { Path = path, Recursive = true });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_outside, "inner", "keep.txt")));
        AssertSwapOutcome();
    }

    [TestMethod]
    public void CreateDirectory_ParentSwappedAfterCheck_NeverCreatesOutside()
    {
        var path = Path.Combine(_folder, "made");

        var response = SwappingFiles(path).CreateDirectory(new DirectoryCreateRequest { Path = path });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsFalse(Directory.Exists(Path.Combine(_outside, "made")));
        AssertSwapOutcome();
    }

    [TestMethod]
    public void Rename_TargetParentSwappedAfterCheck_NeverMovesOutside()
    {
        var target = Path.Combine(_folder, "moved.txt");

        var response = SwappingFiles(target).Rename(new FileRenameRequest { Path = Path.Combine(_root, "a.txt"), NewPath = target });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsFalse(File.Exists(Path.Combine(_outside, "moved.txt")));
        AssertSwapOutcome();
    }

    [TestMethod]
    public void Rename_SourceParentSwappedAfterCheck_NeverMovesOutsideFileIn()
    {
        var source = Path.Combine(_folder, "f.txt");

        var response = SwappingFiles(source).Rename(new FileRenameRequest { Path = source, NewPath = Path.Combine(_root, "taken.txt") });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_outside, "f.txt")));
        AssertSwapOutcome();
    }

    // Variants where the swap really happens: the checked entry is opened with share-delete, so it can be
    // moved away and a junction put in its place; the operation must still act on the object it checked.

    [TestMethod]
    public void ListDirectory_CheckedFolderReplacedByJunction_ListsCheckedFolder()
    {
        File.WriteAllText(Path.Combine(_folder, "only-inside.txt"), "x");

        var response = SwappingFiles(_folder, _folder, _outside).ListDirectory(new DirectoryListRequest { Path = _folder });

        Assert.IsTrue(_swapped, "swap did not happen");
        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        CollectionAssert.Contains(response.Entries.Select(e => e.Name).ToArray(), "only-inside.txt");
    }

    [TestMethod]
    public void DeleteRecursive_CheckedFolderReplacedByJunction_DeletesCheckedFolderOnly()
    {
        var path = Path.Combine(_root, "victim");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside.txt"), "x");

        var response = SwappingFiles(path, path, _outside).Delete(new FileDeleteRequest { Path = path, Recursive = true });

        Assert.IsTrue(_swapped, "swap did not happen");
        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_outside, "f.txt")), "content behind the junction was deleted");
    }

    [TestMethod]
    public void Rename_CheckedFolderReplacedByJunction_MovesCheckedFolderOnly()
    {
        var path = Path.Combine(_root, "victim");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside.txt"), "x");
        var target = Path.Combine(_root, "renamed");

        var response = SwappingFiles(path, path, _outside).Rename(new FileRenameRequest { Path = path, NewPath = target });

        Assert.IsTrue(_swapped, "swap did not happen");
        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_outside, "f.txt")), "folder behind the junction was moved");
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "mklink /J failed");
    }
}
