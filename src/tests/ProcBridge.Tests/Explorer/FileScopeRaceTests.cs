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

    /// <summary>Session whose guard swaps <see cref="_folder"/> for a junction to the outside folder after the check of <paramref name="trigger"/>.</summary>
    private FileExplorerProvider SwappingFiles(string trigger)
    {
        var files = new ExplorerSession(new ExplorerPolicy { FileRoots = [_root], Permissions = ReadWrite }, "machine-a").Files;
        files.Guard.AfterScopeCheck = path =>
        {
            if (!string.Equals(path, trigger, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                Directory.Move(_folder, _folder + ".old");
            }
            catch (IOException)
            {
                return; // the checked handle pins the folder: the swap is impossible, which is fine
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            CreateJunction(_folder, _outside);
        };
        return files;
    }

    [TestMethod]
    public void ListDirectory_FolderSwappedAfterCheck_ListsCheckedFolder()
    {
        File.WriteAllText(Path.Combine(_folder, "only-inside.txt"), "x");

        var response = SwappingFiles(_folder).ListDirectory(new DirectoryListRequest { Path = _folder });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        CollectionAssert.Contains(response.Entries.Select(e => e.Name).ToArray(), "only-inside.txt");
    }

    [TestMethod]
    public void ReadFile_ParentSwappedAfterCheck_NeverReadsOutside()
    {
        var path = Path.Combine(_folder, "f.txt");

        var response = SwappingFiles(path).ReadFile(new FileReadRequest { Path = path, Count = 100 });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual("inside", System.Text.Encoding.ASCII.GetString(response.Data));
    }

    [TestMethod]
    public void Stat_ParentSwappedAfterCheck_ReportsCheckedFile()
    {
        var path = Path.Combine(_folder, "f.txt");

        var response = SwappingFiles(path).Stat(new FileStatRequest { Path = path });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual(6, response.Entry!.Size);
        Assert.AreEqual("f.txt", response.Entry.Name);
    }

    [TestMethod]
    public void Delete_ParentSwappedAfterCheck_NeverDeletesOutside()
    {
        var path = Path.Combine(_folder, "f.txt");

        SwappingFiles(path).Delete(new FileDeleteRequest { Path = path });

        Assert.IsTrue(File.Exists(Path.Combine(_outside, "f.txt")));
    }

    [TestMethod]
    public void DeleteRecursive_FolderSwappedAfterCheck_NeverDeletesOutside()
    {
        var path = Path.Combine(_folder, "inner");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(_outside, "inner"));
        File.WriteAllText(Path.Combine(_outside, "inner", "keep.txt"), "keep");

        SwappingFiles(path).Delete(new FileDeleteRequest { Path = path, Recursive = true });

        Assert.IsTrue(File.Exists(Path.Combine(_outside, "inner", "keep.txt")));
    }

    [TestMethod]
    public void CreateDirectory_ParentSwappedAfterCheck_NeverCreatesOutside()
    {
        var path = Path.Combine(_folder, "made");

        SwappingFiles(path).CreateDirectory(new DirectoryCreateRequest { Path = path });

        Assert.IsFalse(Directory.Exists(Path.Combine(_outside, "made")));
    }

    [TestMethod]
    public void Rename_TargetParentSwappedAfterCheck_NeverMovesOutside()
    {
        var target = Path.Combine(_folder, "moved.txt");

        SwappingFiles(target).Rename(new FileRenameRequest { Path = Path.Combine(_root, "a.txt"), NewPath = target });

        Assert.IsFalse(File.Exists(Path.Combine(_outside, "moved.txt")));
    }

    [TestMethod]
    public void Rename_SourceParentSwappedAfterCheck_NeverMovesOutsideFileIn()
    {
        var source = Path.Combine(_folder, "f.txt");

        SwappingFiles(source).Rename(new FileRenameRequest { Path = source, NewPath = Path.Combine(_root, "taken.txt") });

        Assert.IsTrue(File.Exists(Path.Combine(_outside, "f.txt")));
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
