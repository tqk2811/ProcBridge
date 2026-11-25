using ProcBridge.Agent;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Tests.Lan;

/// <summary>Commits of a file machine B holds open for machine A (M8): in place through the right handle, safely repeated.</summary>
[TestClass]
public sealed class AgentShareHeldTests
{
    private const string UploadId = "0123456789abcdef0123456789abcdef";
    private const uint ShareReadWrite = 0x3;

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

    private static byte[] Pattern(int length, byte seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31 + seed);
        return data;
    }

    /// <summary>Reads past the held handles (they share reading and writing, not a plain read open).</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static string ReadText(string path) => System.Text.Encoding.UTF8.GetString(ReadShared(path));

    private static FsVersion VersionOf(string path)
    {
        var info = new FileInfo(path);
        return new FsVersion { LastWriteTime = info.LastWriteTimeUtc.ToFileTimeUtc(), Size = info.Length };
    }

    [TestMethod]
    public async Task Commit_GoesThroughTheNamedHold_AndALockOfAnotherHoldRefusesItUntouched()
    {
        using var folder = new TempFolder("share");
        string file = folder.WriteFile("a.txt", "old content"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        using AgentShare.HeldFile first = share.OpenHeld("a.txt", ShareReadWrite, write: true);
        using AgentShare.HeldFile second = share.OpenHeld("a.txt", ShareReadWrite, write: true);
        await second.LockAsync(0, 4, exclusive: true, TimeSpan.Zero, default);

        share.WriteBlock("a.txt", UploadId, 0, "new content"u8);
        // The writer of the first handle commits: the second one's lock covers the range, nothing may be written.
        Assert.AreEqual(NtStatus.LockNotGranted, StatusOf(() => share.Commit("a.txt", UploadId, 11, null, null, ConflictPolicy.Overwrite, first.Id)));
        second.Unlock(0, 4);
        Assert.AreEqual("old content", ReadText(file));

        // The writer holding the lock commits through its own handle.
        await second.LockAsync(0, 4, exclusive: true, TimeSpan.Zero, default);
        share.WriteBlock("a.txt", UploadId, 0, "new content"u8);
        share.Commit("a.txt", UploadId, 11, null, null, ConflictPolicy.Overwrite, second.Id);
        second.Unlock(0, 4);
        Assert.AreEqual("new content", ReadText(file));
    }

    [TestMethod]
    public void Commit_FailingHalfWayInPlace_IsWrittenWholeWhenRepeated_NotSeenAsAConflict()
    {
        using var folder = new TempFolder("share");
        byte[] old = Pattern(1024 * 1024, 1);
        byte[] upload = Pattern(1024 * 1024, 7);
        string file = folder.WriteFile("a.bin", old);
        var share = new AgentShare("src", folder.Path, writable: true);
        FsVersion start = VersionOf(file);
        using AgentShare.HeldFile held = share.OpenHeld("a.bin", ShareReadWrite, write: true);

        using (var local = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            // A user of machine B locks a range past the first block written: the commit stops half way.
            local.Lock(600 * 1024, 4096);
            share.WriteBlock("a.bin", UploadId, 0, upload);
            Assert.AreEqual(NtStatus.LockNotGranted, StatusOf(() => share.Commit("a.bin", UploadId, upload.Length, start, null, ConflictPolicy.KeepBoth, held.Id)));
            local.Unlock(600 * 1024, 4096);
        }

        // The write-back resends the same upload: the half written file is not a change of machine B.
        share.WriteBlock("a.bin", UploadId, 0, upload);
        FsCommitResponse response = share.Commit("a.bin", UploadId, upload.Length, start, null, ConflictPolicy.KeepBoth, held.Id);
        Assert.IsNull(response.ConflictPath);
        CollectionAssert.AreEqual(upload, ReadShared(file));
    }

    [TestMethod]
    public void Commit_OfAnIncompleteUpload_IsRefused_NotFilledWithZeros()
    {
        using var folder = new TempFolder("share");
        string file = folder.WriteFile("a.txt", "0123456789"u8.ToArray());
        var share = new AgentShare("src", folder.Path, writable: true);
        using AgentShare.HeldFile held = share.OpenHeld("a.txt", ShareReadWrite, write: true);

        share.WriteBlock("a.txt", UploadId, 0, "abcde"u8);
        Assert.AreEqual(NtStatus.UnexpectedNetworkError, StatusOf(() => share.Commit("a.txt", UploadId, 10, null, null, ConflictPolicy.Overwrite, held.Id)));
        Assert.AreEqual("0123456789", ReadText(file));
    }

    [TestMethod]
    public async Task ClosingTheHeldHandle_WaitsForTheCommitWritingThroughIt()
    {
        using var folder = new TempFolder("share");
        byte[] upload = Pattern(48 * 1024 * 1024, 3);
        string file = folder.WriteFile("big.bin", new byte[upload.Length]);
        var share = new AgentShare("src", folder.Path, writable: true);
        AgentShare.HeldFile held = share.OpenHeld("big.bin", ShareReadWrite, write: true);
        share.WriteBlock("big.bin", UploadId, 0, upload);

        Task<FsCommitResponse> commit = Task.Run(() => share.Commit("big.bin", UploadId, upload.Length, null, null, ConflictPolicy.Overwrite, held.Id));
        // Close (FsCloseHandle, or the hold connection ends) while the commit is writing.
        byte[] first = new byte[1];
        while (!commit.IsCompleted)
        {
            using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                reader.ReadExactly(first);
            if (first[0] == upload[0])
                break;
        }
        held.Dispose();

        await commit;
        CollectionAssert.AreEqual(upload, ReadShared(file));
    }
}
