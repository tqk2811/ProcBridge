using System.Reflection;
using MessagePack;
using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

[TestClass]
public sealed class ExplorerMessagePackTests
{
    [TestMethod]
    public void EveryRequestAndResponse_IsMessagePackObject()
    {
        var types = typeof(ExplorerSession).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "ProcBridge.Agent.Explorer.Models" && !t.IsAbstract && t != typeof(ExplorerAuditEntry))
            .ToList();

        Assert.IsTrue(types.Count > 20);
        foreach (var type in types)
            Assert.IsNotNull(type.GetCustomAttribute<MessagePackObjectAttribute>(), type.Name);
    }

    [TestMethod]
    public void DirectoryListResponse_RoundTrips()
    {
        var original = new DirectoryListResponse
        {
            Status = ExplorerStatus.OutOfScope,
            Error = "no",
            Path = @"C:\x",
            Entries = [new FileEntry { Name = "a", FullPath = @"C:\x\a", Size = 5, IsReparsePoint = true, LastWriteTimeUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) }],
        };

        var copy = MessagePackSerializer.Deserialize<DirectoryListResponse>(MessagePackSerializer.Serialize(original));

        Assert.AreEqual(ExplorerStatus.OutOfScope, copy.Status);
        Assert.AreEqual("no", copy.Error);
        Assert.AreEqual(@"C:\x", copy.Path);
        var entry = copy.Entries.Single();
        Assert.AreEqual(@"C:\x\a", entry.FullPath);
        Assert.AreEqual(5, entry.Size);
        Assert.IsTrue(entry.IsReparsePoint);
        Assert.AreEqual(original.Entries[0].LastWriteTimeUtc, entry.LastWriteTimeUtc);
    }

    [TestMethod]
    public void RegistrySetValueRequest_RoundTrips()
    {
        var original = new RegistrySetValueRequest
        {
            Path = @"HKCU\Software\X",
            Value = new RegistryValue { Name = "m", Type = RegistryValueType.MultiString, Strings = ["a", "b"] },
        };

        var copy = MessagePackSerializer.Deserialize<RegistrySetValueRequest>(MessagePackSerializer.Serialize(original));

        Assert.AreEqual(original.Path, copy.Path);
        Assert.AreEqual(RegistryValueType.MultiString, copy.Value.Type);
        CollectionAssert.AreEqual(new[] { "a", "b" }, copy.Value.Strings!.ToArray());
        Assert.IsNull(copy.Value.Number);
    }

    [TestMethod]
    public void ExplorerPolicy_RoundTrips()
    {
        var original = new ExplorerPolicy
        {
            FileRoots = [@"D:\Share"],
            RegistryRoots = [@"HKCU\Software\X"],
            Permissions = ExplorerPermissions.FileRead | ExplorerPermissions.ProcessKill,
        };

        var copy = MessagePackSerializer.Deserialize<ExplorerPolicy>(MessagePackSerializer.Serialize(original));

        CollectionAssert.AreEqual(original.FileRoots.ToArray(), copy.FileRoots.ToArray());
        CollectionAssert.AreEqual(original.RegistryRoots.ToArray(), copy.RegistryRoots.ToArray());
        Assert.AreEqual(original.Permissions, copy.Permissions);
    }

    [TestMethod]
    public void DefaultPolicy_GrantsNothing()
    {
        var policy = new ExplorerPolicy();

        Assert.AreEqual(ExplorerPermissions.None, policy.Permissions);
        Assert.AreEqual(0, policy.FileRoots.Count);
        Assert.AreEqual(0, policy.RegistryRoots.Count);
    }
}
