using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ProcBridge.Agent;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Crypto.Identity;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Tests.Crypto;

/// <summary>
/// Files written by the stores before they shared <c>ProtectedStoreFile</c> must still load. Each file here is built
/// byte by byte the way the old code wrote it (<c>magic | 0x01 | DPAPI(CurrentUser, identity entropy, payload)</c>),
/// without going through any store code, so a change to the header, the entropy, the scope or the payload format fails.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class ProtectedStoreCompatTests
{
    private static readonly byte[] _entropy = Encoding.ASCII.GetBytes("ProcBridge identity key v1");

    private static byte[] OldFile(string magic, byte[] payload)
    {
        byte[] blob = ProtectedData.Protect(payload, _entropy, DataProtectionScope.CurrentUser);
        return [.. Encoding.ASCII.GetBytes(magic), 1, .. blob];
    }

    [TestMethod]
    public void IdentityKeyStore_LoadsOldFormatFile()
    {
        using var folder = new TempFolder("compat");
        using var key = IdentityKey.Generate();
        string path = folder.WriteFile("identity.key", OldFile("PBIK", key.ExportPrivateKey()));

        using var loaded = new IdentityKeyStore(path, new DpapiKeyProtector()).Load();

        Assert.IsTrue(key.PublicKey.SequenceEqual(loaded.PublicKey));
    }

    [TestMethod]
    public void IdentityKeyStore_SaveWritesOldFormat()
    {
        using var folder = new TempFolder("compat");
        string path = folder.Combine("identity.key");
        using var key = IdentityKey.Generate();

        new IdentityKeyStore(path, new DpapiKeyProtector()).Save(key);

        byte[] data = File.ReadAllBytes(path);
        Assert.IsTrue(data.AsSpan(0, 5).SequenceEqual("PBIK\x01"u8));
        byte[] plain = ProtectedData.Unprotect(data[5..], _entropy, DataProtectionScope.CurrentUser);
        Assert.IsTrue(plain.SequenceEqual(key.ExportPrivateKey()));
    }

    [TestMethod]
    public void PairedMachineStore_LoadsOldFormatFile()
    {
        using var folder = new TempFolder("compat");
        using var key = IdentityKey.Generate();
        string json = $$"""
            [{"Fingerprint":"{{key.Fingerprint}}","PublicKey":"{{Convert.ToBase64String(key.PublicKey)}}","Name":"machine-b","Address":"10.0.0.2:5000","PairedAtUtc":"2026-01-02T03:04:05Z"}]
            """;
        string path = folder.WriteFile("paired.bin", OldFile("PBPM", Encoding.UTF8.GetBytes(json)));

        var store = new PairedMachineStore(path, new DpapiKeyProtector());

        Assert.IsTrue(store.IsTrusted(key.PublicKey));
        var machine = store.Find(key.Fingerprint)!;
        Assert.AreEqual("machine-b", machine.Name);
        Assert.AreEqual("10.0.0.2:5000", machine.Address);
        Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), machine.PairedAtUtc.ToUniversalTime());
    }

    [TestMethod]
    public void ExplorerPolicyStore_LoadsOldFormatFile()
    {
        using var folder = new TempFolder("compat");
        // MessagePack: map{ "fp-a": [ ["D:\\s"], [], 1 ] }
        byte[] payload = [0x81, 0xA4, .. "fp-a"u8, 0x93, 0x91, 0xA4, .. "D:\\s"u8, 0x90, 0x01];
        string path = folder.WriteFile("policies.bin", OldFile("PBEP", payload));

        var policy = new ExplorerPolicyStore(path, new DpapiKeyProtector()).Get("fp-a");

        CollectionAssert.AreEqual(new[] { "D:\\s" }, policy.FileRoots.ToArray());
        Assert.AreEqual(0, policy.RegistryRoots.Count);
        Assert.AreEqual((ExplorerPermissions)1, policy.Permissions);
    }

    [TestMethod]
    public void Stores_RejectOtherMagicOrVersion()
    {
        using var folder = new TempFolder("compat");
        byte[] file = OldFile("PBPM", "[]"u8.ToArray());
        string wrongMagic = folder.WriteFile("wrong-magic.bin", file);
        file[4] = 2;
        string wrongVersion = folder.WriteFile("wrong-version.bin", file);

        Assert.ThrowsExactly<InvalidDataException>(() => new ExplorerPolicyStore(wrongMagic, new DpapiKeyProtector()));
        Assert.ThrowsExactly<InvalidDataException>(() => new PairedMachineStore(wrongVersion, new DpapiKeyProtector()));
        Assert.ThrowsExactly<InvalidDataException>(() => new IdentityKeyStore(wrongMagic, new DpapiKeyProtector()).Load());
    }
}
