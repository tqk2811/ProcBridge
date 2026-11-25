using ProcBridge.Agent;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;

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
