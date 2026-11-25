using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Crypto.Noise.Enums;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Tests.Crypto;

internal static class CryptoTestHelper
{
    public static readonly byte[] Prologue = "ProcBridge test v1"u8.ToArray();

    /// <summary>Runs a full handshake in memory and returns both sessions.</summary>
    public static (SecureSession Initiator, SecureSession Responder) Handshake(
        NoisePattern pattern,
        IdentityKey initiatorKey,
        IdentityKey responderKey,
        Func<byte[], bool>? initiatorVerifier = null,
        Func<byte[], bool>? responderVerifier = null)
    {
        using var initiator = new NoiseHandshake(
            pattern, isInitiator: true, initiatorKey, Prologue,
            pattern == NoisePattern.IK ? responderKey.PublicKey : default, initiatorVerifier);
        using var responder = new NoiseHandshake(
            pattern, isInitiator: false, responderKey, Prologue, default, responderVerifier);
        Run(initiator, responder);
        return (initiator.CreateSession(), responder.CreateSession());
    }

    public static void Run(NoiseHandshake initiator, NoiseHandshake responder)
    {
        while (!initiator.IsCompleted || !responder.IsCompleted)
        {
            var (writer, reader) = initiator.IsMyTurn ? (initiator, responder) : (responder, initiator);
            reader.ReadMessage(writer.WriteMessage(ReadOnlySpan<byte>.Empty));
        }
    }

    public static Func<byte[], bool> Trust(IdentityKey key)
    {
        var expected = key.PublicKey.ToArray();
        return remote => remote.AsSpan().SequenceEqual(expected);
    }
}
