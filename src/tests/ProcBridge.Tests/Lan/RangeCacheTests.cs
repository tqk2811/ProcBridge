using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>M8: large files cached by ranges (sparse copy, blocks downloaded on demand, prefetch, completion in place).</summary>
[TestClass]
public sealed class RangeCacheTests
{
    private const long OneGigabyte = 1L << 30;
    private const int BlockSize = 256 * 1024;

    private static readonly TransportOptions Transport = new() { ConnectionCount = 4, ConnectTimeout = TimeSpan.FromSeconds(5), RequestTimeout = TimeSpan.FromSeconds(30) };

    public TestContext TestContext { get; set; } = null!;

    private static AgentServer StartAgent(TempFolder share)
    {
        var server = new AgentServer([new AgentShare("src", share.Path)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        return server;
    }

    private static AgentClient CreateClient(int port) => new(new TcpConnectionFactory("127.0.0.1", port, Transport), "src", Transport);

    private static FileCacheOptions Options(long threshold = 1024 * 1024, int prefetch = 4) => new()
    {
        MetadataTtl = TimeSpan.FromMinutes(5),
        ReadAheadMaxFileSize = 0,
        RangeThreshold = threshold,
        RangeBlockSize = BlockSize,
        RangePrefetchBlocks = prefetch,
    };

    /// <summary>
    /// A sparse file of <paramref name="size"/> bytes on machine B with random data in a few places (cheap on disk, deleted
    /// with the share folder), so ranges read back can be checked against it.
    /// </summary>
    internal static string CreateSparseFile(string path, long size, int seed, params long[] dataOffsets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("FSCTL_SET_SPARSE failed: " + Marshal.GetLastPInvokeError());
        stream.SetLength(size);
        var random = new Random(seed);
        byte[] data = new byte[3 * BlockSize];
        foreach (long offset in dataOffsets)
        {
            random.NextBytes(data);
            stream.Position = offset;
            stream.Write(data, 0, (int)Math.Min(data.Length, size - offset));
        }
        return path;
    }

    private static byte[] ReadAt(string path, long offset, int length)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[length];
        int read = RandomAccess.Read(handle, buffer, offset);
        Assert.AreEqual(length, read);
        return buffer;
    }

    [TestMethod]
    public async Task LargeFile_ReadsOnlyTheRangesAsked()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        long[] offsets = [123_456_789, 700_000_000, OneGigabyte - 2 * BlockSize];
        string remote = CreateSparseFile(share.Combine(@"game\assets.pak"), OneGigabyte, 1, offsets);
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, Options(prefetch: 0));
        try
        {
            CachedItem item = await cache.EnsureAsync("game/assets.pak", allowRanges: true, CancellationToken.None);
            Assert.AreNotEqual(0u, item.RangeId, "A file above the threshold is cached by ranges.");
            var info = new FileInfo(item.LocalPath);
            Assert.AreEqual(OneGigabyte, info.Length, "The sparse copy has machine B's size at once.");
            Assert.IsTrue(info.Attributes.HasFlag(FileAttributes.SparseFile));
            Assert.AreEqual(0, cache.RangeBytesDownloaded, "Nothing is downloaded before a read.");

            // 1 MB at a random (unaligned) offset in the 1 GB file.
            long offset = offsets[0] + 1000;
            bool complete = await cache.FetchRangeAsync(item.RangeId, offset, 1024 * 1024, whole: false, CancellationToken.None);
            Assert.IsFalse(complete);
            CollectionAssert.AreEqual(ReadAt(remote, offset, 1024 * 1024), ReadAt(item.LocalPath, offset, 1024 * 1024));
            long oneMegabyte = cache.RangeBytesDownloaded;
            TestContext.WriteLine($"Reading 1 MB at a random offset of a 1 GB file downloaded {oneMegabyte} bytes.");
            Assert.IsLessThanOrEqualTo(1024 * 1024 + BlockSize, oneMegabyte, "Only the blocks covering the read are downloaded.");

            // 16 scattered 64 KB reads (1 MB in total).
            var random = new Random(7);
            for (int i = 0; i < 16; i++)
            {
                long at = (long)(random.NextDouble() * (OneGigabyte - 65536));
                await cache.FetchRangeAsync(item.RangeId, at, 65536, whole: false, CancellationToken.None);
            }
            long scattered = cache.RangeBytesDownloaded - oneMegabyte;
            TestContext.WriteLine($"16 random reads of 64 KB (1 MB) downloaded {scattered} bytes (block {BlockSize}).");
            Assert.IsLessThanOrEqualTo(16L * 2 * BlockSize, scattered);

            // The end of the file (a short last block) reads right too.
            long tail = OneGigabyte - 100_000;
            await cache.FetchRangeAsync(item.RangeId, tail, 100_000, whole: false, CancellationToken.None);
            CollectionAssert.AreEqual(ReadAt(remote, tail, 100_000), ReadAt(item.LocalPath, tail, 100_000));
        }
        finally
        {
            cache.AbandonRanges();
        }
    }

    [TestMethod]
    public async Task SequentialReads_PrefetchTheNextBlocks()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        CreateSparseFile(share.Combine("video.bin"), 64L * 1024 * 1024, 2, 0);
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, Options(prefetch: 4));
        try
        {
            CachedItem item = await cache.EnsureAsync("video.bin", allowRanges: true, CancellationToken.None);
            await cache.FetchRangeAsync(item.RangeId, 0, 4096, whole: false, CancellationToken.None);
            Assert.AreEqual(BlockSize, cache.RangeBytesDownloaded, "A first read is not a sequence yet.");
            await cache.FetchRangeAsync(item.RangeId, 4096, 4096, whole: false, CancellationToken.None);
            // The second read continues the first: four more blocks follow in the background.
            long expected = 5L * BlockSize;
            for (int i = 0; i < 200 && cache.RangeBytesDownloaded < expected; i++)
                await Task.Delay(25);
            Assert.AreEqual(expected, cache.RangeBytesDownloaded);

            // A random jump does not prefetch.
            await cache.FetchRangeAsync(item.RangeId, 40L * 1024 * 1024, 4096, whole: false, CancellationToken.None);
            await Task.Delay(200);
            Assert.AreEqual(expected + BlockSize, cache.RangeBytesDownloaded);
        }
        finally
        {
            cache.AbandonRanges();
        }
    }

    [TestMethod]
    public async Task BlockAskedAgainAfterItCameIn_IsNotDownloadedTwice_EvenOnceTheCopyIsComplete()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        CreateSparseFile(share.Combine("one.bin"), 3L * BlockSize, 4, 0);
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        FsEntry entry = await client.StatAsync("one.bin", CancellationToken.None);
        RangeFile file = RangeFile.Create(1, "one.bin", entry, cacheDir.Combine("one.bin"), client, BlockSize, 0);
        try
        {
            await file.CompleteAsync(1, CancellationToken.None);
            Assert.IsTrue(file.IsComplete);
            long downloaded = file.BytesDownloaded;

            // A reader that saw block 1 missing right before it came in (and the copy closed its write handle) asks for it.
            var fetch = (Func<long, Task>)Delegate.CreateDelegate(typeof(Func<long, Task>), file,
                typeof(RangeFile).GetMethod("Fetch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
            await fetch(1);
            Assert.AreEqual(downloaded, file.BytesDownloaded);
        }
        finally
        {
            file.Abandon();
        }
    }

    [TestMethod]
    public async Task UnfinishedCopyReplacedByANewVersion_ClosesItsWriteHandle()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        string remote = CreateSparseFile(share.Combine("data.bin"), 8L * 1024 * 1024, 5, 0);
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, Options());
        try
        {
            CachedItem first = await cache.EnsureAsync("data.bin", allowRanges: true, CancellationToken.None);
            await cache.FetchRangeAsync(first.RangeId, 0, 4096, whole: false, CancellationToken.None);
            string oldCopy = first.LocalPath;
            using (var stream = new FileStream(remote, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                stream.SetLength(9L * 1024 * 1024);
            cache.Invalidate("data.bin");

            CachedItem second = await cache.EnsureAsync("data.bin", allowRanges: true, CancellationToken.None);
            Assert.AreNotEqual(first.RangeId, second.RangeId);
            // The old copy's missing blocks fail at once (no download of a version machine B no longer has).
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => cache.FetchRangeAsync(first.RangeId, 4L * 1024 * 1024, 4096, whole: false, CancellationToken.None));
        }
        finally
        {
            cache.AbandonRanges();
        }
    }

    [TestMethod]
    public async Task WholeOpen_CompletesTheSparseCopyInPlace()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        string remote = CreateSparseFile(share.Combine("db.bin"), 8L * 1024 * 1024, 3, 0, 5L * 1024 * 1024);
        File.SetLastWriteTimeUtc(remote, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, Options());
        try
        {
            CachedItem ranged = await cache.EnsureAsync("db.bin", allowRanges: true, CancellationToken.None);
            Assert.AreNotEqual(0u, ranged.RangeId);
            await cache.FetchRangeAsync(ranged.RangeId, 0, 10, whole: false, CancellationToken.None);
            using (File.OpenHandle(ranged.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // A writer (or a mapping) needs the whole file: completed in place while a reader keeps its handle.
                CachedItem whole = await cache.EnsureAsync("db.bin", allowRanges: false, CancellationToken.None);
                Assert.AreEqual(0u, whole.RangeId);
                Assert.AreEqual(ranged.LocalPath, whole.LocalPath);
            }
            CollectionAssert.AreEqual(File.ReadAllBytes(remote), File.ReadAllBytes(ranged.LocalPath));
            Assert.AreEqual(8L * 1024 * 1024, cache.RangeBytesDownloaded, "Each block is downloaded once.");
            Assert.AreEqual(File.GetLastWriteTimeUtc(remote), File.GetLastWriteTimeUtc(ranged.LocalPath), "The completed copy has machine B's times.");
            Assert.IsTrue(await cache.FetchRangeAsync(ranged.RangeId, 0, 1, whole: false, CancellationToken.None), "Hooks holding the id learn it is complete.");

            // Complete copies are reused like a whole download, ranged or not.
            long requests = client.Pool.GetRequestsPerSlot().Sum();
            CachedItem again = await cache.EnsureAsync("db.bin", allowRanges: true, CancellationToken.None);
            Assert.AreEqual(0u, again.RangeId);
            Assert.AreEqual(requests, client.Pool.GetRequestsPerSlot().Sum());
        }
        finally
        {
            cache.AbandonRanges();
        }
    }

    [TestMethod]
    public async Task FetchWhole_And_SmallFilesStayWhole()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        string remote = CreateSparseFile(share.Combine("lib.dll"), 3L * 1024 * 1024 + 17, 4, 0, 2L * 1024 * 1024);
        share.WriteFile("small.txt", new byte[1000]);
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, Options());
        try
        {
            CachedItem small = await cache.EnsureAsync("small.txt", allowRanges: true, CancellationToken.None);
            Assert.AreEqual(0u, small.RangeId, "Files under the threshold are downloaded whole.");

            CachedItem item = await cache.EnsureAsync("lib.dll", allowRanges: true, CancellationToken.None);
            Assert.IsTrue(await cache.FetchRangeAsync(item.RangeId, 0, 0, whole: true, CancellationToken.None));
            CollectionAssert.AreEqual(File.ReadAllBytes(remote), File.ReadAllBytes(item.LocalPath));
        }
        finally
        {
            cache.AbandonRanges();
        }
    }

    [TestMethod]
    public async Task ChangedOnMachineB_FailsTheMissingRanges()
    {
        using var share = new TempFolder("rng");
        using var cacheDir = new TempFolder("cache");
        string remote = CreateSparseFile(share.Combine("data.bin"), 4L * 1024 * 1024, 5, 0);
        await using AgentServer server = StartAgent(share);
        await using AgentClient client = CreateClient(server.Port);
        var cache = new FileCache(client, cacheDir.Path, Options());
        try
        {
            CachedItem item = await cache.EnsureAsync("data.bin", allowRanges: true, CancellationToken.None);
            await cache.FetchRangeAsync(item.RangeId, 0, 10, whole: false, CancellationToken.None);
            using (var stream = new FileStream(remote, FileMode.Open, FileAccess.Write))
                stream.SetLength(5L * 1024 * 1024);

            var error = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => cache.FetchRangeAsync(item.RangeId, 3L * 1024 * 1024, 10, whole: false, CancellationToken.None));
            Assert.AreEqual(ProcBridge.Protocol.Enums.NtStatus.FileVersionChanged, error.Status, "Old and new content must never mix in one copy.");
        }
        finally
        {
            cache.AbandonRanges();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
