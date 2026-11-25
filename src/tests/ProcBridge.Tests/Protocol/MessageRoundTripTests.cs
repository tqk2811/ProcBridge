using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Tests.Protocol;

[TestClass]
public sealed class MessageRoundTripTests
{
    private static FsEntry SampleEntry(string name, uint attributes = 0x20) => new()
    {
        Name = name,
        Attributes = attributes,
        Size = 123456789012,
        CreationTime = 133000000000000000,
        LastAccessTime = 133000000000000001,
        LastWriteTime = 133000000000000002,
        ChangeTime = 133000000000000003,
        FileId = 0xFEEDFACECAFEBEEF,
        AllocationSize = 123456790528,
    };

    private static void AssertEntryEqual(FsEntry expected, FsEntry actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Attributes, actual.Attributes);
        Assert.AreEqual(expected.Size, actual.Size);
        Assert.AreEqual(expected.CreationTime, actual.CreationTime);
        Assert.AreEqual(expected.LastAccessTime, actual.LastAccessTime);
        Assert.AreEqual(expected.LastWriteTime, actual.LastWriteTime);
        Assert.AreEqual(expected.ChangeTime, actual.ChangeTime);
        Assert.AreEqual(expected.FileId, actual.FileId);
        Assert.AreEqual(expected.AllocationSize, actual.AllocationSize);
    }

    [TestMethod]
    public void Hello_RoundTrips()
    {
        var request = new HelloRequest { ProtocolVersion = ProtocolInfo.Version, ClientName = "A", ShareName = "src" };
        HelloRequest copy = ProtocolSerializer.Deserialize<HelloRequest>(ProtocolSerializer.Serialize(request));
        Assert.AreEqual(request.ProtocolVersion, copy.ProtocolVersion);
        Assert.AreEqual(request.ClientName, copy.ClientName);
        Assert.AreEqual(request.ShareName, copy.ShareName);

        var response = new HelloResponse { ProtocolVersion = 1, ServerName = "B", MaxFrameSize = 5, MaxReadSize = 4 };
        HelloResponse responseCopy = ProtocolSerializer.Deserialize<HelloResponse>(ProtocolSerializer.Serialize(response));
        Assert.AreEqual(response.ServerName, responseCopy.ServerName);
        Assert.AreEqual(response.MaxFrameSize, responseCopy.MaxFrameSize);
        Assert.AreEqual(response.MaxReadSize, responseCopy.MaxReadSize);
    }

    [TestMethod]
    public void StatAndList_RoundTrip()
    {
        FsEntry entry = SampleEntry("a.cs");
        FsStatResponse stat = ProtocolSerializer.Deserialize<FsStatResponse>(ProtocolSerializer.Serialize(new FsStatResponse { Entry = entry }));
        AssertEntryEqual(entry, stat.Entry);

        var list = new FsListResponse { Entries = [SampleEntry("x"), SampleEntry("dir", FsEntry.DirectoryAttribute)], HasMore = true };
        FsListResponse listCopy = ProtocolSerializer.Deserialize<FsListResponse>(ProtocolSerializer.Serialize(list));
        Assert.IsTrue(listCopy.HasMore);
        Assert.HasCount(2, listCopy.Entries);
        AssertEntryEqual(list.Entries[1], listCopy.Entries[1]);
        Assert.IsTrue(listCopy.Entries[1].IsDirectory);
        Assert.AreEqual(0L, listCopy.Entries[1].Version.Size);

        FsListRequest request = ProtocolSerializer.Deserialize<FsListRequest>(ProtocolSerializer.Serialize(new FsListRequest { Path = "a/b", StartIndex = 7, MaxCount = 9 }));
        Assert.AreEqual("a/b", request.Path);
        Assert.AreEqual(7, request.StartIndex);
        Assert.AreEqual(9, request.MaxCount);
    }

    [TestMethod]
    public void Read_RoundTripsWithAndWithoutVersion()
    {
        var expected = new FsVersion { LastWriteTime = 42, Size = 1000 };
        FsReadRequest withVersion = ProtocolSerializer.Deserialize<FsReadRequest>(ProtocolSerializer.Serialize(
            new FsReadRequest { Path = "f", Offset = 1L << 40, Length = 4096, ExpectedVersion = expected }));
        Assert.AreEqual(1L << 40, withVersion.Offset);
        Assert.AreEqual(4096, withVersion.Length);
        Assert.AreEqual(expected, withVersion.ExpectedVersion);

        FsReadRequest withoutVersion = ProtocolSerializer.Deserialize<FsReadRequest>(ProtocolSerializer.Serialize(
            new FsReadRequest { Path = "f", Offset = 0, Length = 1 }));
        Assert.IsNull(withoutVersion.ExpectedVersion);

        FsReadResponse response = ProtocolSerializer.Deserialize<FsReadResponse>(ProtocolSerializer.Serialize(new FsReadResponse { Version = expected }));
        Assert.AreEqual(expected, response.Version);
    }

    [TestMethod]
    public void Error_KeepsNtStatus()
    {
        var error = new ErrorResponse { Status = NtStatus.ObjectNameNotFound, Message = "missing" };
        ErrorResponse copy = ProtocolSerializer.Deserialize<ErrorResponse>(ProtocolSerializer.Serialize(error));
        Assert.AreEqual(NtStatus.ObjectNameNotFound, copy.Status);
        Assert.AreEqual("missing", copy.Message);
    }

    [TestMethod]
    public void MalformedHeader_IsProtocolError()
    {
        var e = Assert.ThrowsExactly<ProtocolStatusException>(() => ProtocolSerializer.Deserialize<FsStatRequest>(new byte[] { 0xC1, 0x00 }));
        Assert.AreEqual(NtStatus.ProtocolError, e.Status);
    }
}
