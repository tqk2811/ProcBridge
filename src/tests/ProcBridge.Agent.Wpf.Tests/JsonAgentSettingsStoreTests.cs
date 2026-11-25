using ProcBridge.Agent.Wpf.Core;
using ProcBridge.Agent.Wpf.Core.Models;

namespace ProcBridge.Agent.Wpf.Tests;

[TestClass]
public sealed class JsonAgentSettingsStoreTests
{
    [TestMethod]
    public void SaveThenLoad_RoundTrips_AndMissingFileGivesDefaults()
    {
        string folder = Directory.CreateTempSubdirectory("pb-agent-ui-").FullName;
        try
        {
            var store = new JsonAgentSettingsStore(Path.Combine(folder, "sub", JsonAgentSettingsStore.FileName));
            Assert.IsEmpty(store.Load().Shares);

            var lastSeen = new DateTime(2026, 11, 25, 1, 2, 3, DateTimeKind.Utc);
            store.Save(new AgentUiSettings
            {
                Port = 1234,
                BindAddress = "127.0.0.1",
                MinimizeToTray = true,
                Shares = [new ShareSettings { Name = "src", Path = @"C:\src", AllowWrite = true }],
                LastSeenUtc = new() { ["FP"] = lastSeen },
            });
            AgentUiSettings loaded = store.Load();

            Assert.AreEqual(1234, loaded.Port);
            Assert.AreEqual("127.0.0.1", loaded.BindAddress);
            Assert.IsTrue(loaded.MinimizeToTray);
            Assert.AreEqual(new ShareSettings { Name = "src", Path = @"C:\src", AllowWrite = true }, loaded.Shares.Single());
            Assert.AreEqual(lastSeen, loaded.LastSeenUtc["FP"].ToUniversalTime());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void CorruptedFile_ThrowsInvalidData()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.ThrowsExactly<InvalidDataException>(() => new JsonAgentSettingsStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
