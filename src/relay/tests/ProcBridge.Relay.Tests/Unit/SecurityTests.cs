using ProcBridge.Relay.Services.Common.Security;

namespace ProcBridge.Relay.Tests.Unit;

[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    public void VéCóTiềnTốNgẫuNhiênVàSoSánhHằngThờiGian()
    {
        var a = SecretTokens.Create("pbt_");
        var b = SecretTokens.Create("pbt_");
        Assert.StartsWith("pbt_", a);
        Assert.AreNotEqual(a, b);
        Assert.IsTrue(SecretTokens.FixedTimeEquals(a, a));
        Assert.IsFalse(SecretTokens.FixedTimeEquals(a, b));
    }
}
