using System.Diagnostics;
using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

[TestClass]
public sealed class FileExplorerProviderTests
{
    private const ExplorerPermissions ReadWrite = ExplorerPermissions.FileRead | ExplorerPermissions.FileWrite;

    private string _base = null!;
    private string _root = null!;
    private string _outside = null!;

    [TestInitialize]
    public void Setup()
    {
        _base = Path.Combine(Path.GetTempPath(), "ProcBridgeExplorer_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello world");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
        // Sibling whose name starts with the root name: must not count as inside.
        Directory.CreateDirectory(_root + "2");
        File.WriteAllText(Path.Combine(_root + "2", "x.txt"), "x");
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Remove junctions first so a recursive delete never walks into their targets.
        foreach (var dir in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories)
                     .Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)).ToList())
            Directory.Delete(dir, false);
        Directory.Delete(_base, true);
    }

    private ExplorerSession Session(ExplorerPermissions permissions, RecordingAuditLog? audit = null)
        => new(new ExplorerPolicy { FileRoots = [_root], Permissions = permissions }, "machine-a", audit);

    [TestMethod]
    public void ListDirectory_InsideRoot_ReturnsEntries()
    {
        var response = Session(ExplorerPermissions.FileRead).Files.ListDirectory(new DirectoryListRequest { Path = _root });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "sub" }, response.Entries.Select(e => e.Name).ToArray());
        Assert.IsTrue(response.Entries.Single(e => e.Name == "sub").IsDirectory);
        Assert.AreEqual(11, response.Entries.Single(e => e.Name == "a.txt").Size);
    }

    [TestMethod]
    public void ListDirectory_WithoutFileRead_IsPermissionDenied()
    {
        var response = Session(ExplorerPermissions.FileWrite).Files.ListDirectory(new DirectoryListRequest { Path = _root });

        Assert.AreEqual(ExplorerStatus.PermissionDenied, response.Status);
    }

    [TestMethod]
    public void EmptyRoots_DenyEverything()
    {
        var session = new ExplorerSession(new ExplorerPolicy { Permissions = ExplorerPermissions.FileRead }, "machine-a");

        Assert.AreEqual(ExplorerStatus.OutOfScope, session.Files.ListDirectory(new DirectoryListRequest { Path = _root }).Status);
    }

    [TestMethod]
    [DataRow(@"{root}\..\outside")]
    [DataRow(@"{root}\sub\..\..\outside\secret.txt")]
    [DataRow(@"{root}2\x.txt")]
    [DataRow(@"{outside}")]
    [DataRow(@"C:\Windows")]
    public void PathOutsideRoot_IsOutOfScope(string template)
    {
        var path = template.Replace("{root}", _root).Replace("{outside}", _outside);
        var files = Session(ReadWrite).Files;

        Assert.AreEqual(ExplorerStatus.OutOfScope, files.Stat(new FileStatRequest { Path = path }).Status);
        Assert.AreEqual(ExplorerStatus.OutOfScope, files.ReadFile(new FileReadRequest { Path = path, Count = 10 }).Status);
        Assert.AreEqual(ExplorerStatus.OutOfScope, files.Delete(new FileDeleteRequest { Path = path, Recursive = true }).Status);
    }

    [TestMethod]
    [DataRow(@"\\localhost\c$\Windows", ExplorerStatus.OutOfScope)]
    [DataRow(@"\\?\C:\Windows", ExplorerStatus.OutOfScope)]
    [DataRow(@"//localhost/c$", ExplorerStatus.OutOfScope)]
    [DataRow(@"relative\path", ExplorerStatus.InvalidArgument)]
    [DataRow(@"C:relative", ExplorerStatus.InvalidArgument)]
    [DataRow("", ExplorerStatus.InvalidArgument)]
    public void MalformedPath_IsRejected(string path, ExplorerStatus expected)
    {
        Assert.AreEqual(expected, Session(ExplorerPermissions.FileRead).Files.Stat(new FileStatRequest { Path = path }).Status);
    }

    [TestMethod]
    public void AlternateDataStream_IsRejected()
    {
        var response = Session(ExplorerPermissions.FileRead).Files.ReadFile(new FileReadRequest { Path = Path.Combine(_root, "a.txt:hidden"), Count = 10 });

        Assert.AreEqual(ExplorerStatus.InvalidArgument, response.Status);
    }

    [TestMethod]
    public void JunctionLeadingOutside_IsOutOfScope()
    {
        var junction = Path.Combine(_root, "escape");
        CreateJunction(junction, _outside);
        var files = Session(ExplorerPermissions.FileRead).Files;

        Assert.AreEqual(ExplorerStatus.OutOfScope, files.ListDirectory(new DirectoryListRequest { Path = junction }).Status);
        Assert.AreEqual(ExplorerStatus.OutOfScope, files.ReadFile(new FileReadRequest { Path = Path.Combine(junction, "secret.txt"), Count = 10 }).Status);
        // The junction itself still shows up in the parent listing, marked as a reparse point.
        var entry = files.ListDirectory(new DirectoryListRequest { Path = _root }).Entries.Single(e => e.Name == "escape");
        Assert.IsTrue(entry.IsReparsePoint);
    }

    [TestMethod]
    public void JunctionStayingInside_IsAllowed()
    {
        var junction = Path.Combine(_root, "alias");
        CreateJunction(junction, Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "b");

        var response = Session(ExplorerPermissions.FileRead).Files.ListDirectory(new DirectoryListRequest { Path = junction });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual("b.txt", response.Entries.Single().Name);
    }

    [TestMethod]
    public void NewPathUnderJunctionLeadingOutside_IsOutOfScope()
    {
        var junction = Path.Combine(_root, "escape");
        CreateJunction(junction, _outside);

        var response = Session(ReadWrite).Files.CreateDirectory(new DirectoryCreateRequest { Path = Path.Combine(junction, "new", "deeper") });

        Assert.AreEqual(ExplorerStatus.OutOfScope, response.Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(_outside, "new")));
    }

    [TestMethod]
    public void SymbolicLinkFileLeadingOutside_IsOutOfScope()
    {
        var link = Path.Combine(_root, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.txt"));
        }
        catch (IOException ex)
        {
            Assert.Inconclusive("Creating a symbolic link needs Developer Mode or admin: " + ex.Message);
        }

        var response = Session(ExplorerPermissions.FileRead).Files.ReadFile(new FileReadRequest { Path = link, Count = 10 });

        Assert.AreEqual(ExplorerStatus.OutOfScope, response.Status);
    }

    [TestMethod]
    public void DeleteJunction_RemovesLinkOnly()
    {
        var junction = Path.Combine(_root, "escape");
        CreateJunction(junction, _outside);

        var response = Session(ReadWrite).Files.Delete(new FileDeleteRequest { Path = junction, Recursive = true });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.IsFalse(Directory.Exists(junction));
        Assert.IsTrue(File.Exists(Path.Combine(_outside, "secret.txt")));
    }

    [TestMethod]
    public void ReadFile_ReturnsChunk()
    {
        var files = Session(ExplorerPermissions.FileRead).Files;

        var response = files.ReadFile(new FileReadRequest { Path = Path.Combine(_root, "a.txt"), Offset = 6, Count = 100 });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual("world", System.Text.Encoding.ASCII.GetString(response.Data));
        Assert.AreEqual(11, response.FileLength);
        Assert.AreEqual(0, files.ReadFile(new FileReadRequest { Path = Path.Combine(_root, "a.txt"), Offset = 50, Count = 10 }).Data.Length);
        Assert.AreEqual(ExplorerStatus.NotFound, files.ReadFile(new FileReadRequest { Path = Path.Combine(_root, "none.txt"), Count = 1 }).Status);
    }

    [TestMethod]
    public void WriteOperations_WithoutFileWrite_AreDeniedAndAudited()
    {
        var audit = new RecordingAuditLog();
        var files = Session(ExplorerPermissions.FileRead, audit).Files;
        var file = Path.Combine(_root, "a.txt");

        Assert.AreEqual(ExplorerStatus.PermissionDenied, files.Delete(new FileDeleteRequest { Path = file }).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, files.Rename(new FileRenameRequest { Path = file, NewPath = file + ".bak" }).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, files.CreateDirectory(new DirectoryCreateRequest { Path = Path.Combine(_root, "n") }).Status);

        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual(3, audit.Entries.Count);
        Assert.IsTrue(audit.Entries.All(e => e.Status == ExplorerStatus.PermissionDenied && e.RequesterId == "machine-a"));
    }

    [TestMethod]
    public void WriteOperations_WithFileWrite_Succeed()
    {
        var audit = new RecordingAuditLog();
        var files = Session(ReadWrite, audit).Files;

        Assert.AreEqual(ExplorerStatus.Ok, files.CreateDirectory(new DirectoryCreateRequest { Path = Path.Combine(_root, "made") }).Status);
        Assert.AreEqual(ExplorerStatus.AlreadyExists, files.CreateDirectory(new DirectoryCreateRequest { Path = Path.Combine(_root, "made") }).Status);
        Assert.AreEqual(ExplorerStatus.Ok, files.Rename(new FileRenameRequest { Path = Path.Combine(_root, "a.txt"), NewPath = Path.Combine(_root, "made", "b.txt") }).Status);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "made", "b.txt")));
        Assert.AreEqual(ExplorerStatus.Failed, files.Delete(new FileDeleteRequest { Path = Path.Combine(_root, "made") }).Status);
        Assert.AreEqual(ExplorerStatus.Ok, files.Delete(new FileDeleteRequest { Path = Path.Combine(_root, "made"), Recursive = true }).Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "made")));
        Assert.AreEqual(ExplorerStatus.NotFound, files.Delete(new FileDeleteRequest { Path = Path.Combine(_root, "made") }).Status);
        Assert.AreEqual(6, audit.Entries.Count);
        Assert.AreEqual("Fs.Rename", audit.Entries[2].Action);
    }

    [TestMethod]
    public void RenameOutOfRoot_IsOutOfScope()
    {
        var response = Session(ReadWrite).Files.Rename(new FileRenameRequest { Path = Path.Combine(_root, "a.txt"), NewPath = Path.Combine(_outside, "a.txt") });

        Assert.AreEqual(ExplorerStatus.OutOfScope, response.Status);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "a.txt")));
    }

    [TestMethod]
    public void RootItself_CannotBeChanged()
    {
        var files = Session(ReadWrite).Files;

        Assert.AreEqual(ExplorerStatus.OutOfScope, files.Delete(new FileDeleteRequest { Path = _root, Recursive = true }).Status);
        Assert.AreEqual(ExplorerStatus.OutOfScope, files.Rename(new FileRenameRequest { Path = _root + @"\", NewPath = Path.Combine(_root, "x") }).Status);
        Assert.IsTrue(Directory.Exists(_root));
    }

    [TestMethod]
    public void ListDrives_ReturnsDriveOfRootAndRoots()
    {
        var response = Session(ExplorerPermissions.FileRead).Files.ListDrives(new DrivesRequest());

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual(Path.GetPathRoot(_root), response.Drives.Single().Name, ignoreCase: true);
        CollectionAssert.AreEqual(new[] { _root }, response.Roots.ToArray());
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
