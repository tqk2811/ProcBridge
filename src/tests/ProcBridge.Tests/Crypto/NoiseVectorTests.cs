using System.Text.Json;
using ProcBridge.Crypto.Noise;
using ProcBridge.Crypto.Noise.Enums;

namespace ProcBridge.Tests.Crypto;

/// <summary>Official Noise test vectors (cacophony) for the two patterns ProcBridge uses.</summary>
[TestClass]
public sealed class NoiseVectorTests
{
    [TestMethod]
    [DataRow("Noise_XX_25519_ChaChaPoly_SHA256", NoisePattern.XX)]
    [DataRow("Noise_IK_25519_ChaChaPoly_SHA256", NoisePattern.IK)]
    public void Vector_MatchesExpectedCiphertexts(string protocolName, NoisePattern pattern)
    {
        var vector = LoadVector(protocolName);

        using var initStatic = X25519KeyPair.FromPrivateKey(Hex(vector, "init_static"));
        using var respStatic = X25519KeyPair.FromPrivateKey(Hex(vector, "resp_static"));
        var remoteStatic = pattern == NoisePattern.IK ? Hex(vector, "init_remote_static") : [];

        using var initiator = new NoiseHandshake(
            pattern, true, initStatic, X25519KeyPair.FromPrivateKey(Hex(vector, "init_ephemeral")),
            Hex(vector, "init_prologue"), remoteStatic, null);
        using var responder = new NoiseHandshake(
            pattern, false, respStatic, X25519KeyPair.FromPrivateKey(Hex(vector, "resp_ephemeral")),
            Hex(vector, "resp_prologue"), [], null);

        CipherState? i2r = null, r2i = null, peerI2r = null, peerR2i = null;
        try
        {
            var index = 0;
            foreach (var message in vector.GetProperty("messages").EnumerateArray())
            {
                var payload = Convert.FromHexString(message.GetProperty("payload").GetString()!);
                var expected = Convert.FromHexString(message.GetProperty("ciphertext").GetString()!);
                var initiatorSends = index % 2 == 0;

                if (!initiator.IsCompleted)
                {
                    var (writer, reader) = initiatorSends ? (initiator, responder) : (responder, initiator);
                    var actual = writer.WriteMessage(payload);
                    CollectionAssert.AreEqual(expected, actual, $"handshake message {index}");
                    CollectionAssert.AreEqual(payload, reader.ReadMessage(actual), $"handshake payload {index}");
                    if (initiator.IsCompleted)
                    {
                        Assert.IsTrue(responder.IsCompleted);
                        CollectionAssert.AreEqual(Hex(vector, "handshake_hash"), initiator.HandshakeHash);
                        CollectionAssert.AreEqual(initiator.HandshakeHash, responder.HandshakeHash);
                        (i2r, r2i) = initiator.CreateCipherStates();
                        (peerI2r, peerR2i) = responder.CreateCipherStates();
                    }
                }
                else
                {
                    var cipher = initiatorSends ? i2r! : r2i!;
                    var actual = cipher.EncryptWithAd(ReadOnlySpan<byte>.Empty, payload);
                    CollectionAssert.AreEqual(expected, actual, $"transport message {index}");
                    var peer = initiatorSends ? peerI2r! : peerR2i!;
                    CollectionAssert.AreEqual(payload, peer.DecryptWithAd(ReadOnlySpan<byte>.Empty, actual), $"transport payload {index}");
                }
                index++;
            }
            Assert.IsTrue(initiator.IsCompleted);
        }
        finally
        {
            i2r?.Dispose();
            r2i?.Dispose();
            peerI2r?.Dispose();
            peerR2i?.Dispose();
        }
    }

    private static JsonElement LoadVector(string protocolName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Crypto", "Vectors", "noise-vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (vector.GetProperty("protocol_name").GetString() == protocolName)
                return vector.Clone();
        }
        throw new AssertFailedException($"Vector {protocolName} not found.");
    }

    private static byte[] Hex(JsonElement vector, string name) => Convert.FromHexString(vector.GetProperty(name).GetString()!);
}
