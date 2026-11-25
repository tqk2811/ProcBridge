using ProcBridge.Relay.Testing.Infrastructure;

namespace ProcBridge.Relay.Tests.Unit;

[TestClass]
public sealed class PostgresRequirementTests
{
    [TestMethod]
    [DataRow("1", true)]
    [DataRow("true", false)]
    [DataRow("0", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void ChỉGiáTrị1MớiBắtBuộcCóPostgres(string? value, bool expected)
        => Assert.AreEqual(expected, PostgresTestDatabase.IsRequiredValue(value));
}
