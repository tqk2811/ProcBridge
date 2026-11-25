using ProcBridge.Agent;

namespace ProcBridge.Tests.Lan;

[TestClass]
public sealed class DirectoryListingCacheTests
{
    private static DirectoryListingCache.Snapshot Scan() => new([], []);

    /// <summary>An expired snapshot goes on the next call of any kind, not only when another large listing is kept.</summary>
    [TestMethod]
    public void ExpiredSnapshot_IsDroppedOnTheNextCall()
    {
        var cache = new DirectoryListingCache(TimeSpan.FromMilliseconds(200));
        cache.Get(@"C:\big", firstPage: true, Scan, _ => true);
        Assert.AreEqual(1, cache.Count);

        Thread.Sleep(600);
        cache.Get(@"C:\small", firstPage: true, Scan, _ => false);
        Assert.AreEqual(0, cache.Count);
    }

    /// <summary>The lifetime counts from the last page served, so a listing paged slowly keeps one consistent scan.</summary>
    [TestMethod]
    public void PagesServedFromASnapshot_KeepItAlive()
    {
        var cache = new DirectoryListingCache(TimeSpan.FromSeconds(2));
        cache.Get(@"C:\big", firstPage: true, Scan, _ => true);
        for (int i = 0; i < 2; i++)
        {
            Thread.Sleep(1200);
            cache.Get(@"C:\big", firstPage: false, Scan, _ => true);
        }
        Assert.AreEqual(1, cache.ScanCount);
    }
}
