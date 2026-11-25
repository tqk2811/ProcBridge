using ProcBridge.Agent;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Tests.Lan;

[TestClass]
public sealed class ReadOnlyShareTests
{
    [TestMethod]
    [DataRow("NUL")]
    [DataRow("nul")]
    [DataRow("sub/CON")]
    [DataRow("con.txt")]
    [DataRow("aux.tar.gz")]
    [DataRow("PRN")]
    [DataRow("COM1")]
    [DataRow("com0.log")]
    [DataRow("LPT9")]
    [DataRow("COM\u00B9")]
    [DataRow("lpt\u00B2.txt")]
    [DataRow("COM\u00B3")]
    [DataRow("CONIN$")]
    [DataRow("conout$.txt")]
    [DataRow("NUL .txt")]
    public void DosDeviceNames_AreRejected(string path)
    {
        using var folder = new TempFolder("share");
        var share = new ReadOnlyShare("src", folder.Path);
        var e = Assert.ThrowsExactly<ProtocolStatusException>(() => share.ResolvePath(path));
        Assert.AreEqual(NtStatus.ObjectNameInvalid, e.Status);
    }

    [TestMethod]
    public void StatAndList_ReportFileIdChangeTimeAndAllocationOfMachineB()
    {
        using var folder = new TempFolder("share");
        folder.WriteFile(@"sub\a.txt", "hello"u8.ToArray());
        string path = System.IO.Path.Combine(folder.Path, "sub", "a.txt");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var share = new ReadOnlyShare("src", folder.Path);

        FsEntry stat = share.Stat("sub/a.txt");
        FsEntry listed = share.List("sub", 0, 0).Entries.Single();

        Assert.AreNotEqual(0ul, stat.FileId);
        Assert.AreEqual(stat.FileId, listed.FileId);
        // Setting the write time changes the change time, which is now later than the write time.
        Assert.IsGreaterThan(stat.LastWriteTime, stat.ChangeTime);
        Assert.AreEqual(stat.ChangeTime, listed.ChangeTime);
        Assert.IsNotNull(stat.AllocationSize);
        Assert.AreEqual(stat.AllocationSize, listed.AllocationSize);
        Assert.AreNotEqual(0ul, share.Stat("sub").FileId);
    }

    [TestMethod]
    public void List_PageStaysWithinTheByteBudget_AndPagingCoversEverything()
    {
        using var folder = new TempFolder("share");
        string[] names = Enumerable.Range(0, 200).Select(i => i.ToString("D3") + new string('x', 150)).ToArray();
        foreach (string name in names)
            folder.WriteFile(name, [1]);
        var share = new ReadOnlyShare("src", folder.Path);

        const int Budget = 8 * 1024;
        var seen = new List<string>();
        int start = 0;
        int pages = 0;
        while (true)
        {
            FsListResponse page = share.List("", start, maxCount: 4096, maxResponseBytes: Budget);
            Assert.IsNotEmpty(page.Entries);
            Assert.IsLessThanOrEqualTo(Budget, ProtocolSerializer.Serialize(page).Length);
            seen.AddRange(page.Entries.Select(x => x.Name));
            start += page.Entries.Length;
            pages++;
            if (!page.HasMore)
                break;
        }
        CollectionAssert.AreEqual(names, seen);
        Assert.IsGreaterThan(1, pages);
    }

    [TestMethod]
    [DataRow("console.txt")]
    [DataRow("com10")]
    [DataRow("lpt")]
    [DataRow("nulled")]
    [DataRow("a.nul")]
    [DataRow("conin")]
    public void NamesThatOnlyResembleDevices_AreAccepted(string path)
    {
        using var folder = new TempFolder("share");
        var share = new ReadOnlyShare("src", folder.Path);
        Assert.AreEqual(share.RootPath + "\\" + path, share.ResolvePath(path));
    }
}
