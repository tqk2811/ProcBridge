using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Crypto.Noise.Enums;

namespace ProcBridge.Tests.Crypto;

[TestClass]
public sealed class NoiseHandshakeTests
{
    [TestMethod]
    [DataRow(NoisePattern.XX)]
    [DataRow(NoisePattern.IK)]
    public void Handshake_WithTrustedKeys_AuthenticatesBothSides(NoisePattern pattern)
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();

        var (initiator, responder) = CryptoTestHelper.Handshake(
            pattern, a, b, CryptoTestHelper.Trust(b), CryptoTestHelper.Trust(a));
        using (initiator)
        using (responder)
        {
            Assert.IsTrue(initiator.RemoteStaticPublicKey.SequenceEqual(b.PublicKey));
            Assert.IsTrue(responder.RemoteStaticPublicKey.SequenceEqual(a.PublicKey));
            Assert.AreEqual(b.Fingerprint, initiator.RemoteFingerprint);
            Assert.IsTrue(initiator.HandshakeHash.SequenceEqual(responder.HandshakeHash));
        }
    }

    [TestMethod]
    public void IK_InitiatorUsesWrongResponderKey_IsRejected()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var impostor = IdentityKey.Generate();

        // A believes it talks to B, but the machine answering holds a different key.
        using var initiator = new NoiseHandshake(NoisePattern.IK, true, a, CryptoTestHelper.Prologue, b.PublicKey, CryptoTestHelper.Trust(b));
        using var responder = new NoiseHandshake(NoisePattern.IK, false, impostor, CryptoTestHelper.Prologue);

        var first = initiator.WriteMessage(ReadOnlySpan<byte>.Empty);
        Assert.ThrowsExactly<CryptoProtocolException>(() => responder.ReadMessage(first));
    }

    [TestMethod]
    [DataRow(NoisePattern.XX)]
    [DataRow(NoisePattern.IK)]
    public void Responder_RejectsUnpairedInitiator(NoisePattern pattern)
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var paired = IdentityKey.Generate();

        Assert.ThrowsExactly<CryptoProtocolException>(() =>
            CryptoTestHelper.Handshake(pattern, a, b, CryptoTestHelper.Trust(b), CryptoTestHelper.Trust(paired)));
    }

    [TestMethod]
    public void XX_Initiator_RejectsUnpairedResponder()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var paired = IdentityKey.Generate();

        Assert.ThrowsExactly<CryptoProtocolException>(() =>
            CryptoTestHelper.Handshake(NoisePattern.XX, a, b, CryptoTestHelper.Trust(paired), null));
    }

    [TestMethod]
    public void PrologueMismatch_IsRejected()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var initiator = new NoiseHandshake(NoisePattern.IK, true, a, "v1"u8, b.PublicKey);
        using var responder = new NoiseHandshake(NoisePattern.IK, false, b, "v2"u8);

        var first = initiator.WriteMessage(ReadOnlySpan<byte>.Empty);
        Assert.ThrowsExactly<CryptoProtocolException>(() => responder.ReadMessage(first));
    }

    [TestMethod]
    public void TamperedHandshakeMessage_IsRejected()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var initiator = new NoiseHandshake(NoisePattern.XX, true, a, CryptoTestHelper.Prologue);
        using var responder = new NoiseHandshake(NoisePattern.XX, false, b, CryptoTestHelper.Prologue);

        responder.ReadMessage(initiator.WriteMessage(ReadOnlySpan<byte>.Empty));
        var second = responder.WriteMessage("hello"u8);
        second[^1] ^= 0x01;
        Assert.ThrowsExactly<CryptoProtocolException>(() => initiator.ReadMessage(second));
        // A failed handshake cannot be resumed.
        Assert.ThrowsExactly<InvalidOperationException>(() => initiator.WriteMessage(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void TruncatedHandshakeMessage_IsRejected()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var initiator = new NoiseHandshake(NoisePattern.XX, true, a, CryptoTestHelper.Prologue);
        using var responder = new NoiseHandshake(NoisePattern.XX, false, b, CryptoTestHelper.Prologue);

        var first = initiator.WriteMessage(ReadOnlySpan<byte>.Empty);
        Assert.ThrowsExactly<CryptoProtocolException>(() => responder.ReadMessage(first.AsSpan(0, 10)));
    }

    [TestMethod]
    public void HandshakePayload_IsDelivered()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var initiator = new NoiseHandshake(NoisePattern.IK, true, a, CryptoTestHelper.Prologue, b.PublicKey);
        using var responder = new NoiseHandshake(NoisePattern.IK, false, b, CryptoTestHelper.Prologue);

        CollectionAssert.AreEqual("hello"u8.ToArray(), responder.ReadMessage(initiator.WriteMessage("hello"u8)));
        CollectionAssert.AreEqual("world"u8.ToArray(), initiator.ReadMessage(responder.WriteMessage("world"u8)));
        Assert.IsTrue(initiator.IsCompleted);
        Assert.IsTrue(responder.IsCompleted);
    }
}
