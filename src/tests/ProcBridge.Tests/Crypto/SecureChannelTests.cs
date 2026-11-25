using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise.Enums;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Tests.Crypto;

[TestClass]
public sealed class SecureChannelTests
{
    private static (SecureSession Initiator, SecureSession Responder) CreateSessions()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        return CryptoTestHelper.Handshake(NoisePattern.IK, a, b, CryptoTestHelper.Trust(b), CryptoTestHelper.Trust(a));
    }

    private static (SecureChannel Sender, SecureChannel Receiver) CreatePair(SecureSession initiator, SecureSession responder)
    {
        var sender = initiator.OpenChannel();
        return (sender, responder.AcceptChannel(sender.Id));
    }

    [TestMethod]
    public void SealOpen_RoundTripsBothDirections()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var payload = "Fs.Read secret content"u8.ToArray();
                var frame = a.Seal(payload, "hdr"u8);
                Assert.HasCount(payload.Length + SecureChannel.Overhead, frame);
                Assert.AreEqual(-1, frame.AsSpan().IndexOf(payload.AsSpan()), "plaintext visible on the wire");
                CollectionAssert.AreEqual(payload, b.Open(frame, "hdr"u8));
                CollectionAssert.AreEqual("reply"u8.ToArray(), a.Open(b.Seal("reply"u8)));
                CollectionAssert.AreEqual(Array.Empty<byte>(), b.Open(a.Seal(ReadOnlySpan<byte>.Empty)));
            }
        }
    }

    [TestMethod]
    public void FlippingAnyBit_IsRejected()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var frame = a.Seal("payload!"u8, "hdr"u8);
                for (var bit = 0; bit < frame.Length * 8; bit++)
                {
                    var tampered = (byte[])frame.Clone();
                    tampered[bit / 8] ^= (byte)(1 << (bit % 8));
                    Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(tampered, "hdr"u8), $"bit {bit}");
                }
                // The genuine frame still opens: failures do not advance the counter.
                CollectionAssert.AreEqual("payload!"u8.ToArray(), b.Open(frame, "hdr"u8));
            }
        }
    }

    [TestMethod]
    public void WrongAssociatedData_IsRejected()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var frame = a.Seal("payload"u8, "stream=1"u8);
                Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(frame, "stream=2"u8));
            }
        }
    }

    [TestMethod]
    public void ReplayedFrame_IsRejected()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var first = a.Seal("one"u8);
                var second = a.Seal("two"u8);
                b.Open(first);
                Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(first));
                b.Open(second);
                Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(second));
                Assert.AreEqual(2UL, b.ReceivedFrames);
            }
        }
    }

    [TestMethod]
    public void DroppedOrReorderedFrame_IsRejected()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var first = a.Seal("one"u8);
                var second = a.Seal("two"u8);
                Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(second));
                b.Open(first);
                b.Open(second);
            }
        }
    }

    [TestMethod]
    public void FrameFromAnotherChannel_IsRejected()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a1, b1) = CreatePair(si, sr);
            var (a2, b2) = CreatePair(si, sr);
            using (a1)
            using (b1)
            using (a2)
            using (b2)
            {
                Assert.AreNotEqual(a1.Id, a2.Id);
                var frame = a1.Seal("payload"u8);
                // Same counter (0) on both channels, but keys differ per channel.
                Assert.ThrowsExactly<CryptoProtocolException>(() => b2.Open(frame));
                CollectionAssert.AreEqual("payload"u8.ToArray(), b1.Open(frame));
            }
        }
    }

    [TestMethod]
    public void FrameFromAnotherSession_IsRejected()
    {
        var (si1, sr1) = CreateSessions();
        var (si2, sr2) = CreateSessions();
        using (si1)
        using (sr1)
        using (si2)
        using (sr2)
        {
            using var a = si1.OpenChannel();
            using var b = sr2.AcceptChannel(si2.OpenChannel().Id);
            Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(a.Seal("payload"u8)));
        }
    }

    [TestMethod]
    public void SameChannelsOnBothSides_ProduceDistinctCiphertexts()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            using var c1 = si.OpenChannel();
            using var c2 = si.OpenChannel();
            var payload = new byte[64];
            // Identical plaintext and counter must not give identical ciphertext (no nonce/key reuse).
            CollectionAssert.AreNotEqual(c1.Seal(payload), c2.Seal(payload));
        }
    }

    [TestMethod]
    public void ChannelIds_AreUniqueAndCannotBeReused()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var initiatorIds = Enumerable.Range(0, 4).Select(_ => si.OpenChannel().Id).ToArray();
            var responderIds = Enumerable.Range(0, 4).Select(_ => sr.OpenChannel().Id).ToArray();
            Assert.IsTrue(initiatorIds.All(id => id % 2 == 0));
            Assert.IsTrue(responderIds.All(id => id % 2 == 1));

            sr.AcceptChannel(initiatorIds[0]);
            Assert.ThrowsExactly<CryptoProtocolException>(() => sr.AcceptChannel(initiatorIds[0]), "replayed connection id");
            Assert.ThrowsExactly<CryptoProtocolException>(() => sr.AcceptChannel(responderIds[1]), "own id");
            Assert.ThrowsExactly<CryptoProtocolException>(() => sr.AcceptChannel(1001), "wrong parity");
        }
    }

    [TestMethod]
    public void Rekey_KeepsBothSidesInSyncAndChangesKey()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            si.RekeyInterval = 3;
            sr.RekeyInterval = 3;
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var payload = new byte[32];
                var frames = Enumerable.Range(0, 10).Select(_ => a.Seal(payload)).ToArray();
                foreach (var frame in frames)
                    CollectionAssert.AreEqual(payload, b.Open(frame));
                Assert.AreEqual(10UL, b.ReceivedFrames);
                CollectionAssert.AreEqual(payload, a.Open(b.Seal(payload)));
            }
        }
    }

    [TestMethod]
    public void Rekey_FrameAfterBoundary_UsesNewKey()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            si.RekeyInterval = 2;
            sr.RekeyInterval = 1000;
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                b.Open(a.Seal("0"u8));
                b.Open(a.Seal("1"u8));
                // Sender ratcheted at counter 2, receiver did not: the frame must fail to open.
                Assert.ThrowsExactly<CryptoProtocolException>(() => b.Open(a.Seal("2"u8)));
            }
        }
    }

    [TestMethod]
    public void ManyParallelChannels_ConcurrentTrafficStaysIntact()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            si.RekeyInterval = 50;
            sr.RekeyInterval = 50;
            const int channelCount = 16;
            const int framesPerChannel = 300;
            var pairs = Enumerable.Range(0, channelCount)
                .Select(i => i % 2 == 0 ? CreatePair(si, sr) : CreatePair(sr, si))
                .ToArray();

            Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, pair =>
            {
                var (sender, receiver) = pair;
                for (var n = 0; n < framesPerChannel; n++)
                {
                    var payload = BitConverter.GetBytes(((long)sender.Id << 32) | (uint)n);
                    var opened = receiver.Open(sender.Seal(payload));
                    CollectionAssert.AreEqual(payload, opened);
                }
            });

            foreach (var (sender, receiver) in pairs)
            {
                Assert.AreEqual((ulong)framesPerChannel, receiver.ReceivedFrames);
                sender.Dispose();
                receiver.Dispose();
            }
        }
    }

    [TestMethod]
    public void ConcurrentSealOnOneChannel_NeverReusesCounter()
    {
        var (si, sr) = CreateSessions();
        using (si)
        using (sr)
        {
            var (a, b) = CreatePair(si, sr);
            using (a)
            using (b)
            {
                var frames = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
                Parallel.For(0, 2000, _ => frames.Add(a.Seal("x"u8)));
                var counters = frames.Select(f => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(f)).ToHashSet();
                Assert.HasCount(2000, counters);
                foreach (var frame in frames.OrderBy(f => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(f)))
                    b.Open(frame);
            }
        }
    }
}
