using ProcBridge.Agent;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Tests.Lan;

/// <summary>Write side of a share (M3): staging uploads, version checked commits, and the NTFS statuses of refusals.</summary>
[TestClass]
public sealed class AgentShareWriteTests
{
    private const string UploadId = "0123456789abcdef0123456789abcdef";

    private static NtStatus StatusOf(Action action)
    {
        try
        {
            action();
            return NtStatus.Success;
        }
        catch (ProtocolStatusException e)
        {
            return e.Status;
        }
    }

    [TestMethod]
    public void ReadOnlyShare_RefusesEveryWrite()
    {
        using var folder = new TempFolder("share");
        folder.WriteFile("a.txt", "a"u8.ToArray());
        var share = new AgentShare("src", folder.Path);

        Assert.AreEqual(NtStatus.MediaWriteProtected, StatusOf(() => share.Create("b.txt", false, 0)));
        Assert.AreEqual(NtStatus.MediaWriteProtected, StatusOf(() => share.WriteBlock("a.txt", UploadId, 0, "x"u8)));
        Assert.AreEqual(NtStatus.MediaWriteProtected, StatusOf(() => share.Rename("a.txt", "b.txt", false)));
        Assert.AreEqual(NtStatus.MediaWriteProtected, StatusOf(() => share.Delete("a.txt")));
        Assert.AreEqual(NtStatus.MediaWriteProtected, StatusOf(() => share.SetInfo("a.txt", 0x2, null, null, null, null)));
        Assert.AreEqual("a", File.ReadAllText(folder.Combine("a.txt")));
    }

    [TestMethod]
    public void Create_FileAndDirectory_WithCollisionAndMissingParent()
    {
        using var folder = new TempFolder("share");
        var share = new AgentShare("src", folder.Path, writable: true);

        FsEntry dir = share.Create("sub", true, 0);
        FsEntry file = share.Create("sub/new.txt", false, 0x2 /* hidden */);

        Assert.IsTrue(dir.IsDirectory);
        Assert.AreEqual(0L, file.Size);
        Assert.AreNotEqual(0u, file.Attributes & 0x2);
        Assert.AreEqual(NtStatus.ObjectNameCollision, StatusOf(() => share.Create("sub/new.txt", false, 0)));
        Assert.AreEqual(NtStatus.ObjectNameCollision, StatusOf(() => share.Create("SUB", true, 0)));
        Assert.AreEqual(NtStatus.ObjectPathNotFound, StatusOf(() => share.Create("missing/x.txt", false, 0)));
        Assert.AreEqual(NtStatus.ObjectNameInvalid, StatusOf(() => share.Create("../escape.txt", false, 0)));
        Assert.AreEqual(NtStatus.ObjectNameInvalid, StatusOf(() => share.Create("sub/NUL", false, 0)));
        Assert.AreEqual(NtStatus.AccessDenied, StatusOf(() => share.Create("", true, 0)));
    }

    [TestMethod]
    public void UploadAndCommit_ReplacesContentKeepsIdentityAndHidesStaging()
    {
        using var folder = new TempFolder("share");
        string path = folder.WriteFile("a.txt", "old content"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        FsEntry before = share.Stat("a.txt");

        share.WriteBlock("a.txt", UploadId, 4, "4567"u8);
        share.WriteBlock("a.txt", UploadId, 0, "0123"u8);
        Assert.AreEqual(1, share.List("", 0, 0).Entries.Length, "staging file must not be listed");
        long time = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc).ToFileTimeUtc();
        FsCommitResponse commit = share.Commit("a.txt", UploadId, 8, before.Version, time, ConflictPolicy.KeepBoth);

        Assert.IsNull(commit.ConflictPath);
        Assert.AreEqual("01234567", File.ReadAllText(path));
        Assert.AreEqual(time, commit.Entry.LastWriteTime);
        Assert.AreEqual(before.CreationTime, commit.Entry.CreationTime);
        Assert.AreEqual(1, Directory.GetFileSystemEntries(folder.Path).Length);
    }

    [TestMethod]
    public void Commit_AfterChangeOnMachineB_KeepsBothByDefault()
    {
        using var folder = new TempFolder("share");
        string path = folder.WriteFile("a.txt", "base"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        FsVersion seen = share.Stat("a.txt").Version;
        File.WriteAllText(path, "changed on B");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        share.WriteBlock("a.txt", UploadId, 0, "from A"u8);
        FsCommitResponse commit = share.Commit("a.txt", UploadId, 6, seen, null, ConflictPolicy.KeepBoth);

        Assert.IsNotNull(commit.ConflictPath);
        StringAssert.StartsWith(commit.ConflictPath, "a.txt.procbridge-conflict-");
        Assert.AreEqual("changed on B", File.ReadAllText(path));
        Assert.AreEqual("from A", File.ReadAllText(folder.Combine(commit.ConflictPath)));
    }

    [TestMethod]
    public void Commit_AfterChangeOnMachineB_OverwritePolicyLetsMachineAWin()
    {
        using var folder = new TempFolder("share");
        string path = folder.WriteFile("a.txt", "base"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        FsVersion seen = share.Stat("a.txt").Version;
        File.WriteAllText(path, "changed on B");

        share.WriteBlock("a.txt", UploadId, 0, "from A"u8);
        FsCommitResponse commit = share.Commit("a.txt", UploadId, 6, seen, null, ConflictPolicy.Overwrite);

        Assert.IsNull(commit.ConflictPath);
        Assert.AreEqual("from A", File.ReadAllText(path));
    }

    [TestMethod]
    public void SetInfo_ChangesTimesAttributesAndSize()
    {
        using var folder = new TempFolder("share");
        string path = folder.WriteFile("a.txt", "0123456789"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        long created = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        long written = new DateTime(2002, 2, 2, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

        FsEntry entry = share.SetInfo("a.txt", 0x1 /* read only */, created, null, written, 4);

        Assert.AreEqual(4L, entry.Size);
        Assert.AreEqual(created, entry.CreationTime);
        Assert.AreEqual(written, entry.LastWriteTime);
        Assert.AreNotEqual(0u, entry.Attributes & 0x1);
        // Times of a read-only file can still be changed through the share.
        long later = new DateTime(2003, 3, 3, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        Assert.AreEqual(later, share.SetInfo("a.txt", null, null, null, later, null).LastWriteTime);
        Assert.AreNotEqual(0, (int)(File.GetAttributes(path) & FileAttributes.ReadOnly));
        Assert.AreEqual(FileAttributes.Normal, (FileAttributes)share.SetInfo("a.txt", 0x80, null, null, null, null).Attributes);
    }

    [TestMethod]
    public void RenameAndDelete_FollowNtfsRules()
    {
        using var folder = new TempFolder("share");
        folder.WriteFile("a.txt", "a"u8.ToArray());
        folder.WriteFile("b.txt", "b"u8.ToArray());
        folder.WriteFile(@"dir\inner.txt", "i"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);

        Assert.AreEqual(NtStatus.ObjectNameCollision, StatusOf(() => share.Rename("a.txt", "b.txt", false)));
        Assert.AreEqual("A.TXT", share.Rename("a.txt", "A.TXT", false).Name);
        share.Rename("A.TXT", "b.txt", true);
        Assert.AreEqual("a", File.ReadAllText(folder.Combine("b.txt")));
        Assert.AreEqual(NtStatus.AccessDenied, StatusOf(() => share.Rename("b.txt", "dir", true)));
        Assert.AreEqual("moved", share.Rename("dir", "moved", false).Name);
        Assert.AreEqual(NtStatus.ObjectPathNotFound, StatusOf(() => share.Rename("b.txt", "nowhere/b.txt", false)));

        Assert.AreEqual(NtStatus.DirectoryNotEmpty, StatusOf(() => share.Delete("moved")));
        Assert.IsTrue(share.Delete("moved/inner.txt"));
        Assert.IsTrue(share.Delete("moved"));
        File.SetAttributes(folder.Combine("b.txt"), FileAttributes.ReadOnly);
        Assert.AreEqual(NtStatus.CannotDelete, StatusOf(() => share.Delete("b.txt")));
        Assert.AreEqual(NtStatus.ObjectNameNotFound, StatusOf(() => share.Delete("missing.txt")));
        Assert.AreEqual(NtStatus.AccessDenied, StatusOf(() => share.Delete("")));
    }

    [TestMethod]
    public void WriteBlock_ParallelBlocksOfOneUpload_DoNotCollide()
    {
        // The Broker uploads several blocks of one file at once (AgentClient.UploadAsync).
        using var folder = new TempFolder("share");
        folder.WriteFile("big.bin", []);
        var share = new AgentShare("src", folder.Path, writable: true);
        const int blockSize = 4096;
        const int blocks = 256;
        byte[] content = new byte[blockSize * blocks];
        new Random(1).NextBytes(content);

        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => share.WriteBlock("big.bin", UploadId, (long)i * blockSize, content.AsSpan(i * blockSize, blockSize)));
        share.Commit("big.bin", UploadId, content.Length, null, null, ConflictPolicy.KeepBoth);

        CollectionAssert.AreEqual(content, File.ReadAllBytes(folder.Combine("big.bin")));
    }

    [TestMethod]
    public void Commit_RepeatedAfterSuccess_IsNotAConflict()
    {
        // A commit whose reply was lost is sent again (same upload id, same expected version).
        using var folder = new TempFolder("share");
        folder.WriteFile("a.txt", "base"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        FsVersion start = share.Stat("a.txt").Version;
        long time = new DateTime(2020, 5, 5, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

        share.WriteBlock("a.txt", UploadId, 0, "new content"u8);
        FsCommitResponse first = share.Commit("a.txt", UploadId, 11, start, time, ConflictPolicy.KeepBoth);
        share.WriteBlock("a.txt", UploadId, 0, "new content"u8);
        FsCommitResponse again = share.Commit("a.txt", UploadId, 11, start, time, ConflictPolicy.KeepBoth);

        Assert.IsNull(first.ConflictPath);
        Assert.IsNull(again.ConflictPath);
        Assert.AreEqual(first.Entry.Version, again.Entry.Version);
        Assert.AreEqual("new content", File.ReadAllText(folder.Combine("a.txt")));
        CollectionAssert.AreEqual(new[] { "a.txt" }, Directory.GetFiles(folder.Path).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void Commit_TwoConflictsInARow_KeepBothUploads()
    {
        using var folder = new TempFolder("share");
        folder.WriteFile("a.txt", "base"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        var stale = new FsVersion { LastWriteTime = 1, Size = 1 };

        share.WriteBlock("a.txt", UploadId, 0, "one"u8);
        FsCommitResponse first = share.Commit("a.txt", UploadId, 3, stale, null, ConflictPolicy.KeepBoth);
        const string second = "fedcba9876543210fedcba9876543210";
        share.WriteBlock("a.txt", second, 0, "two"u8);
        FsCommitResponse other = share.Commit("a.txt", second, 3, stale, null, ConflictPolicy.KeepBoth);

        Assert.IsNotNull(first.ConflictPath);
        Assert.IsNotNull(other.ConflictPath);
        Assert.AreNotEqual(first.ConflictPath, other.ConflictPath);
        Assert.AreEqual("one", File.ReadAllText(folder.Combine(first.ConflictPath)));
        Assert.AreEqual("two", File.ReadAllText(folder.Combine(other.ConflictPath)));
        Assert.AreEqual("base", File.ReadAllText(folder.Combine("a.txt")));
    }

    [TestMethod]
    public void Delete_WithExpectedFileId_LeavesAnotherFileAlone()
    {
        using var folder = new TempFolder("share");
        folder.WriteFile("a.txt", "old"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        ulong oldId = share.Stat("a.txt").FileId;
        // Replaced by another file under the same name since the id was taken.
        folder.WriteFile("b.txt", "new"u8.ToArray());
        File.Move(folder.Combine("b.txt"), folder.Combine("a.txt"), overwrite: true);
        Assert.AreNotEqual(oldId, share.Stat("a.txt").FileId);

        Assert.IsFalse(share.Delete("a.txt", oldId));
        Assert.AreEqual("new", File.ReadAllText(folder.Combine("a.txt")));
        Assert.IsTrue(share.Delete("a.txt", share.Stat("a.txt").FileId));
        Assert.IsFalse(File.Exists(folder.Combine("a.txt")));
    }
}
