using ProcBridge.Relay.Services.Common.Security;

namespace ProcBridge.Relay.Tests.Unit;

[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    public void MậtKhẩuĐúngThìKhớpSaiThìKhông()
    {
        var hasher = new PasswordHasher(1000);
        var stored = hasher.Hash("mat-khau-1");
        Assert.IsTrue(hasher.Verify("mat-khau-1", stored));
        Assert.IsFalse(hasher.Verify("mat-khau-2", stored));
        Assert.AreNotEqual(stored, hasher.Hash("mat-khau-1"), "Mỗi lần băm phải có salt mới.");
        Assert.IsFalse(hasher.Verify("mat-khau-1", "rac$1$x$y"));
    }

    [TestMethod]
    public void HashGiảCóCùngChiPhíVàKhôngKhớpMậtKhẩuNào()
    {
        var hasher = new PasswordHasher(1000);
        StringAssert.StartsWith(hasher.DummyHash, "pbkdf2-sha256$1000$");
        Assert.HasCount(4, hasher.DummyHash.Split('$'));
        Assert.IsFalse(hasher.Verify("mat-khau-1", hasher.DummyHash));
    }

    [TestMethod]
    public void TokenCóTiềnTốNgẫuNhiênVàBămỔnĐịnh()
    {
        var a = SecretTokens.Create("pbd_");
        var b = SecretTokens.Create("pbd_");
        Assert.StartsWith("pbd_", a);
        Assert.AreNotEqual(a, b);
        Assert.AreEqual(SecretTokens.Hash(a), SecretTokens.Hash(a));
        Assert.AreEqual(64, SecretTokens.Hash(a).Length);
        Assert.IsTrue(SecretTokens.FixedTimeEquals(a, a));
        Assert.IsFalse(SecretTokens.FixedTimeEquals(a, b));
    }
}
