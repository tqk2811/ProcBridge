using ProcBridge.Relay.Cli;

namespace ProcBridge.Relay.Tests.Unit;

[TestClass]
public sealed class CliArgumentsTests
{
    [TestMethod]
    public void TáchĐượcTừVịTríVàTuỳChọn()
    {
        var a = new CliArguments(["--url", "http://relay/", "share", "grant", "abc", "--help"]);
        CollectionAssert.AreEqual(new[] { "share", "grant", "abc" }, a.Positionals.ToArray());
        Assert.AreEqual("http://relay/", a.Option("url"));
        Assert.IsTrue(a.HasOption("help"));
        Assert.IsNull(a.Option("help"));
        Assert.ThrowsExactly<CliUsageException>(() => a.Required(3, "deviceId"));
        Assert.ThrowsExactly<CliUsageException>(() => a.RequiredGuid(2, "shareId"));
    }
}
