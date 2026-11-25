using System.Runtime.Versioning;
using System.Security.Cryptography;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Identity.Interfaces;

namespace ProcBridge.Tests.Crypto;

[TestClass]
public sealed class IdentityKeyTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "ProcBridgeTests", Guid.NewGuid().ToString("N"), "identity.key");

    [TestMethod]
    public void Fingerprint_IsStableAndFormatted()
    {
        using var key = IdentityKey.Generate();
        Assert.MatchesRegex("^[A-Z2-7]{4}(-[A-Z2-7]{4}){7}$", key.Fingerprint);
        Assert.AreEqual(key.Fingerprint, KeyFingerprint.Compute(key.PublicKey));
        using var other = IdentityKey.Generate();
        Assert.AreNotEqual(key.Fingerprint, other.Fingerprint);
    }

    [TestMethod]
    public void Fingerprint_KnownValue()
    {
        // Pins the format so a refactor cannot silently change what users compare.
        // Expected value computed independently with Python hashlib + base64.b32encode.
        Assert.AreEqual("U3SZ-74VH-JGDM-YVUW-LHNI-TUMT-DYRO-KDYH", KeyFingerprint.Compute(new byte[32]));
    }

    [TestMethod]
    public void Store_LoadOrCreate_PersistsSameKey()
    {
        var path = NewPath();
        try
        {
            var store = new IdentityKeyStore(path, new XorProtector());
            Assert.IsFalse(store.Exists);
            using var created = store.LoadOrCreate();
            Assert.IsTrue(store.Exists);
            using var loaded = store.LoadOrCreate();
            Assert.IsTrue(created.PublicKey.SequenceEqual(loaded.PublicKey));

            // The raw private key must not appear in the file.
            var file = File.ReadAllBytes(path);
            Assert.AreEqual(-1, file.AsSpan().IndexOf(created.ExportPrivateKey()));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void Store_RejectsForeignFile()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6]);
            var store = new IdentityKeyStore(path, new XorProtector());
            Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void Store_WithDpapi_RoundTrips()
    {
        var path = NewPath();
        try
        {
            var store = new IdentityKeyStore(path, new DpapiKeyProtector());
            using var created = store.LoadOrCreate();
            using var loaded = store.Load();
            Assert.IsTrue(created.PublicKey.SequenceEqual(loaded.PublicKey));

            var otherEntropy = new IdentityKeyStore(path, new DpapiKeyProtector("other"u8.ToArray()));
            Assert.Throws<CryptographicException>(() => otherEntropy.Load());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void Store_LoadOrCreate_ConcurrentProcesses_AgreeOnOneKey()
    {
        var path = NewPath();
        try
        {
            for (var round = 0; round < 5; round++)
            {
                if (File.Exists(path))
                    File.Delete(path);
                const int racers = 4;
                // Both "processes" pass the Exists check and generate a key before either writes.
                var protector = new BarrierProtector(racers);
                var keys = new IdentityKey[racers];
                Parallel.For(0, racers, new ParallelOptions { MaxDegreeOfParallelism = racers },
                    i => keys[i] = new IdentityKeyStore(path, protector).LoadOrCreate());
                try
                {
                    using var stored = new IdentityKeyStore(path, new XorProtector()).Load();
                    foreach (var key in keys)
                        Assert.IsTrue(stored.PublicKey.SequenceEqual(key.PublicKey), "a racer returned a key that is not on disk");
                    Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length, "temp files left behind");
                }
                finally
                {
                    foreach (var key in keys)
                        key?.Dispose();
                }
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void PrivateKey_RoundTrips()
    {
        using var key = IdentityKey.Generate();
        using var copy = IdentityKey.FromPrivateKey(key.ExportPrivateKey());
        Assert.IsTrue(key.PublicKey.SequenceEqual(copy.PublicKey));
    }

    /// <summary>Test-only protector: reversible and visibly different from the input.</summary>
    private sealed class XorProtector : IKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(x => (byte)(x ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }

    /// <summary>Like <see cref="XorProtector"/>, but Protect waits until every racer reaches it.</summary>
    private sealed class BarrierProtector(int participants) : IKeyProtector
    {
        private readonly Barrier _barrier = new(participants);
        private readonly XorProtector _inner = new();

        public byte[] Protect(byte[] plaintext)
        {
            _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return _inner.Protect(plaintext);
        }

        public byte[] Unprotect(byte[] protectedData) => _inner.Unprotect(protectedData);
    }
}
