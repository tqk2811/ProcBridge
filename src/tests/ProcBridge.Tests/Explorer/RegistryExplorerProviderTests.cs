using Microsoft.Win32;
using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

/// <summary>Works only below HKCU\Software\ProcBridgeTest, one random key per test, removed afterwards.</summary>
[TestClass]
public sealed class RegistryExplorerProviderTests
{
    private const string TestParent = @"Software\ProcBridgeTest";

    private string _subKey = null!;
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _subKey = TestParent + @"\" + Guid.NewGuid().ToString("N");
        _root = @"HKCU\" + _subKey;
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue("Str", "text", RegistryValueKind.String);
        key.SetValue("Exp", "%TEMP%\\x", RegistryValueKind.ExpandString);
        key.SetValue("Dw", unchecked((int)0xFFFFFFFE), RegistryValueKind.DWord);
        key.SetValue("Qw", 1L << 40, RegistryValueKind.QWord);
        key.SetValue("Multi", new[] { "a", "b" }, RegistryValueKind.MultiString);
        key.SetValue("Bin", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        key.CreateSubKey("Child")!.Dispose();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(TestParent);
        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
    }

    private ExplorerSession Session(ExplorerPermissions permissions, RecordingAuditLog? audit = null)
        => new(new ExplorerPolicy { RegistryRoots = [_root], Permissions = permissions }, "machine-a", audit);

    [TestMethod]
    public void List_ReturnsValuesWithTypes()
    {
        var response = Session(ExplorerPermissions.RegistryRead).Registry.List(new RegistryListRequest { Path = "HKEY_CURRENT_USER\\" + _subKey + "\\" });

        Assert.AreEqual(ExplorerStatus.Ok, response.Status, response.Error);
        Assert.AreEqual(_root, response.Path);
        CollectionAssert.AreEqual(new[] { "Child" }, response.SubKeys.ToArray());
        var values = response.Values.ToDictionary(v => v.Name);
        Assert.AreEqual("text", values["Str"].Text);
        Assert.AreEqual(RegistryValueType.ExpandString, values["Exp"].Type);
        Assert.AreEqual("%TEMP%\\x", values["Exp"].Text);
        Assert.AreEqual(0xFFFFFFFEL, values["Dw"].Number);
        Assert.AreEqual(1L << 40, values["Qw"].Number);
        CollectionAssert.AreEqual(new[] { "a", "b" }, values["Multi"].Strings!.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, values["Bin"].Bytes);
    }

    [TestMethod]
    [DataRow(@"HKCU\Software")]
    [DataRow(@"HKLM\{sub}")]
    [DataRow(@"HKCU\{sub}X")]
    public void OutsideRoot_IsOutOfScope(string template)
    {
        var path = template.Replace("{sub}", _subKey);

        Assert.AreEqual(ExplorerStatus.OutOfScope, Session(ExplorerPermissions.RegistryRead).Registry.List(new RegistryListRequest { Path = path }).Status);
    }

    [TestMethod]
    [DataRow(@"{root}\..")]
    [DataRow(@"{root}\.\Child")]
    [DataRow(@"{root}\\Child")]
    [DataRow(@"HKXX\Software")]
    [DataRow("")]
    public void MalformedPath_IsInvalid(string template)
    {
        var path = template.Replace("{root}", _root);

        Assert.AreEqual(ExplorerStatus.InvalidArgument, Session(ExplorerPermissions.RegistryRead).Registry.List(new RegistryListRequest { Path = path }).Status);
    }

    [TestMethod]
    public void WithoutPermissions_ReadAndWriteAreDenied()
    {
        var registry = Session(ExplorerPermissions.None).Registry;

        Assert.AreEqual(ExplorerStatus.PermissionDenied, registry.List(new RegistryListRequest { Path = _root }).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, registry.SetValue(new RegistrySetValueRequest { Path = _root, Value = new RegistryValue { Name = "Str", Type = RegistryValueType.String, Text = "changed" } }).Status);
        Assert.AreEqual(ExplorerStatus.PermissionDenied, registry.DeleteKey(new RegistryDeleteKeyRequest { Path = _root + @"\Child" }).Status);
        using var key = Registry.CurrentUser.OpenSubKey(_subKey)!;
        Assert.AreEqual("text", key.GetValue("Str"));
        Assert.AreEqual(1, key.SubKeyCount);
    }

    [TestMethod]
    public void WriteOperations_WithRegistryWrite_Succeed()
    {
        var audit = new RecordingAuditLog();
        var registry = Session(ExplorerPermissions.RegistryWrite, audit).Registry;

        Assert.AreEqual(ExplorerStatus.Ok, registry.SetValue(new RegistrySetValueRequest { Path = _root, Value = new RegistryValue { Name = "New", Type = RegistryValueType.DWord, Number = 0xFFFFFFFF } }).Status);
        Assert.AreEqual(ExplorerStatus.Ok, registry.DeleteValue(new RegistryDeleteValueRequest { Path = _root, Name = "Str" }).Status);
        Assert.AreEqual(ExplorerStatus.NotFound, registry.DeleteValue(new RegistryDeleteValueRequest { Path = _root, Name = "Str" }).Status);
        Assert.AreEqual(ExplorerStatus.Ok, registry.CreateKey(new RegistryCreateKeyRequest { Path = _root + @"\Child\Grand" }).Status);
        Assert.AreEqual(ExplorerStatus.Failed, registry.DeleteKey(new RegistryDeleteKeyRequest { Path = _root + @"\Child" }).Status);
        Assert.AreEqual(ExplorerStatus.Ok, registry.DeleteKey(new RegistryDeleteKeyRequest { Path = _root + @"\Child", Recursive = true }).Status);
        Assert.AreEqual(ExplorerStatus.InvalidArgument, registry.SetValue(new RegistrySetValueRequest { Path = _root, Value = new RegistryValue { Name = "Bad", Type = RegistryValueType.DWord, Number = 1L << 33 } }).Status);
        Assert.AreEqual(ExplorerStatus.InvalidArgument, registry.SetValue(new RegistrySetValueRequest { Path = _root, Value = new RegistryValue { Name = "Bad", Type = RegistryValueType.String } }).Status);

        using var key = Registry.CurrentUser.OpenSubKey(_subKey)!;
        Assert.AreEqual(-1, key.GetValue("New"));
        Assert.IsNull(key.GetValue("Str"));
        Assert.IsNull(key.GetValue("Bad"));
        Assert.AreEqual(0, key.SubKeyCount);
        Assert.AreEqual(8, audit.Entries.Count);
    }

    [TestMethod]
    public void RootKeyItself_CannotBeDeleted()
    {
        var response = Session(ExplorerPermissions.RegistryWrite).Registry.DeleteKey(new RegistryDeleteKeyRequest { Path = _root, Recursive = true });

        Assert.AreEqual(ExplorerStatus.OutOfScope, response.Status);
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.IsNotNull(key);
    }

    [TestMethod]
    public void SetValue_WithoutValue_IsInvalidArgumentAndAudited()
    {
        // A request decoded from the wire may carry nil for a required member: it must come back as a status, never as an exception.
        var audit = new RecordingAuditLog();
        var request = new RegistrySetValueRequest { Path = _root, Value = null! };

        var response = Session(ExplorerPermissions.RegistryWrite, audit).Registry.SetValue(request);

        Assert.AreEqual(ExplorerStatus.InvalidArgument, response.Status);
        Assert.AreEqual(ExplorerStatus.InvalidArgument, audit.Entries.Single().Status);
    }
}
