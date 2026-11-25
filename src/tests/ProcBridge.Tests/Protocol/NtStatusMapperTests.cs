using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Helpers;

namespace ProcBridge.Tests.Protocol;

[TestClass]
public sealed class NtStatusMapperTests
{
    [TestMethod]
    public void MapsCommonExceptions()
    {
        Assert.AreEqual(NtStatus.ObjectNameNotFound, NtStatusMapper.FromException(new FileNotFoundException()));
        Assert.AreEqual(NtStatus.ObjectPathNotFound, NtStatusMapper.FromException(new DirectoryNotFoundException()));
        Assert.AreEqual(NtStatus.AccessDenied, NtStatusMapper.FromException(new UnauthorizedAccessException()));
        Assert.AreEqual(NtStatus.Cancelled, NtStatusMapper.FromException(new OperationCanceledException()));
        Assert.AreEqual(NtStatus.FileVersionChanged, NtStatusMapper.FromException(new ProtocolStatusException(NtStatus.FileVersionChanged, null)));
        Assert.AreEqual(NtStatus.Unsuccessful, NtStatusMapper.FromException(new InvalidOperationException()));
    }

    [TestMethod]
    public void MapsWin32IoErrors()
    {
        Assert.AreEqual(NtStatus.SharingViolation, NtStatusMapper.FromException(new IOException("x", unchecked((int)0x80070020))));
        Assert.AreEqual(NtStatus.NotADirectory, NtStatusMapper.FromException(new IOException("x", unchecked((int)0x8007010B))));
        Assert.AreEqual(NtStatus.Unsuccessful, NtStatusMapper.FromException(new IOException("x", unchecked((int)0x80004005))));
    }

    [TestMethod]
    public async Task SharingViolationFromRealFile_IsMapped()
    {
        string path = Path.Combine(Path.GetTempPath(), "pb-share-" + Guid.NewGuid().ToString("N"));
        await using var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var e = Assert.Throws<IOException>(() => File.OpenRead(path));
        Assert.AreEqual(NtStatus.SharingViolation, NtStatusMapper.FromException(e));
    }
}
