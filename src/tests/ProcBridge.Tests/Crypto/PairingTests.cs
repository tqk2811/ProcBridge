using ProcBridge.Crypto;
using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise.Enums;
using ProcBridge.Crypto.Pairing;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Tests.Crypto;

[TestClass]
public sealed class PairingTests
{
    private static (SecureSession A, SecureSession B) PairingSessions(IdentityKey a, IdentityKey b)
        => CryptoTestHelper.Handshake(NoisePattern.XX, a, b);

    [TestMethod]
    public void CorrectCode_PairsBothSides_AndKeysWorkForIK()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        var (sa, sb) = PairingSessions(a, b);
        using (sa)
        using (sb)
        {
            var initiator = new PairingInitiator(sa);
            var responder = new PairingResponder(sb);
            var nonce = responder.ReceiveCommit(initiator.CreateCommit());
            responder.ReceiveReveal(initiator.ReceiveNonce(nonce));

            Assert.IsNotNull(responder.Code);
            Assert.MatchesRegex(@"^\d{6}$", responder.Code);
            var typed = responder.Code[..3] + " " + responder.Code[3..];
            responder.ReceiveConfirm(initiator.ConfirmCode(typed));
            Assert.IsTrue(initiator.IsPaired);
            Assert.IsTrue(responder.IsConfirmedByPeer);
            Assert.IsFalse(responder.IsPaired, "B must wait for the user to accept");
            responder.Accept();
            Assert.IsTrue(responder.IsPaired);

            CollectionAssert.AreEqual(b.PublicKey.ToArray(), initiator.PeerPublicKey);
            CollectionAssert.AreEqual(a.PublicKey.ToArray(), responder.PeerPublicKey);
            Assert.AreEqual(b.Fingerprint, initiator.PeerFingerprint);

            // Stored keys now authenticate an IK session.
            var trustedByA = initiator.PeerPublicKey;
            var trustedByB = responder.PeerPublicKey;
            var (ia, ib) = CryptoTestHelper.Handshake(
                NoisePattern.IK, a, b, k => k.AsSpan().SequenceEqual(trustedByA), k => k.AsSpan().SequenceEqual(trustedByB));
            ia.Dispose();
            ib.Dispose();
        }
    }

    [TestMethod]
    public void WrongCode_IsRejected_AndCannotRetry()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        var (sa, sb) = PairingSessions(a, b);
        using (sa)
        using (sb)
        {
            var initiator = new PairingInitiator(sa);
            var responder = new PairingResponder(sb);
            responder.ReceiveReveal(initiator.ReceiveNonce(responder.ReceiveCommit(initiator.CreateCommit())));

            var wrong = ((int.Parse(responder.Code!) + 1) % 1_000_000).ToString("D6");
            Assert.ThrowsExactly<CryptoProtocolException>(() => initiator.ConfirmCode(wrong));
            Assert.IsFalse(initiator.IsPaired);
            Assert.ThrowsExactly<InvalidOperationException>(() => initiator.ConfirmCode(responder.Code!));
        }
    }

    [TestMethod]
    public void ForgedConfirm_IsRejected()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        var (sa, sb) = PairingSessions(a, b);
        using (sa)
        using (sb)
        {
            var initiator = new PairingInitiator(sa);
            var responder = new PairingResponder(sb);
            responder.ReceiveReveal(initiator.ReceiveNonce(responder.ReceiveCommit(initiator.CreateCommit())));
            var forged = new byte[33];
            forged[0] = 4;
            Assert.ThrowsExactly<CryptoProtocolException>(() => responder.ReceiveConfirm(forged));
            Assert.IsFalse(responder.IsConfirmedByPeer);
        }
    }

    [TestMethod]
    public void ManInTheMiddle_RelayingMessages_FailsCommitment()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var m = IdentityKey.Generate();
        var (sa, smA) = PairingSessions(a, m);
        var (smB, sb) = PairingSessions(m, b);
        using (sa)
        using (smA)
        using (smB)
        using (sb)
        {
            var initiator = new PairingInitiator(sa);
            var responder = new PairingResponder(sb);
            // M forwards A's messages to B unchanged; commitments are bound to each session's handshake hash.
            var nonce = responder.ReceiveCommit(initiator.CreateCommit());
            var reveal = initiator.ReceiveNonce(nonce);
            Assert.ThrowsExactly<CryptoProtocolException>(() => responder.ReceiveReveal(reveal));
        }
    }

    [TestMethod]
    public void ManInTheMiddle_RunningTwoPairings_ShowsDifferentCodes()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        using var m = IdentityKey.Generate();
        var (sa, smA) = PairingSessions(a, m);
        var (smB, sb) = PairingSessions(m, b);
        using (sa)
        using (smA)
        using (smB)
        using (sb)
        {
            var initiator = new PairingInitiator(sa);
            var mAsResponder = new PairingResponder(smA);
            var mAsInitiator = new PairingInitiator(smB);
            var responder = new PairingResponder(sb);

            responder.ReceiveReveal(mAsInitiator.ReceiveNonce(responder.ReceiveCommit(mAsInitiator.CreateCommit())));
            mAsResponder.ReceiveReveal(initiator.ReceiveNonce(mAsResponder.ReceiveCommit(initiator.CreateCommit())));

            // The user types the code shown on B into A. M cannot steer A's code (1 in 10^6 chance).
            Assert.AreNotEqual(responder.Code, mAsResponder.Code);
            Assert.ThrowsExactly<CryptoProtocolException>(() => initiator.ConfirmCode(responder.Code!));
        }
    }

    [TestMethod]
    public void OutOfOrderCalls_Throw()
    {
        using var a = IdentityKey.Generate();
        using var b = IdentityKey.Generate();
        var (sa, sb) = PairingSessions(a, b);
        using (sa)
        using (sb)
        {
            var initiator = new PairingInitiator(sa);
            var responder = new PairingResponder(sb);
            Assert.ThrowsExactly<InvalidOperationException>(() => initiator.ConfirmCode("000000"));
            Assert.ThrowsExactly<InvalidOperationException>(() => responder.Accept());
            Assert.ThrowsExactly<CryptoProtocolException>(() => responder.ReceiveCommit(new byte[5]));
        }
    }
}
