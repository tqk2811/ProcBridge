using ProcBridge.Protocol;

namespace ProcBridge.Tests;

[TestClass]
public sealed class ProtocolInfoTests
{
    [TestMethod]
    public void Version_IsPositive()
    {
        Assert.IsGreaterThan(0, ProtocolInfo.Version);
    }
}
