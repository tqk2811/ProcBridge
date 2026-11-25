using MessagePack;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol;

/// <summary>
/// MessagePack settings for control messages. Formatters come from the MessagePack-CSharp source generator
/// (picked up first by the standard resolver), so no reflection emit runs for the protocol types.
/// </summary>
public static class ProtocolSerializer
{
    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, Options);

    /// <summary>Deserializes a header; malformed input becomes <see cref="NtStatus.ProtocolError"/>.</summary>
    public static T Deserialize<T>(ReadOnlyMemory<byte> data)
    {
        try
        {
            return MessagePackSerializer.Deserialize<T>(data, Options)
                ?? throw new ProtocolStatusException(NtStatus.ProtocolError, $"Empty {typeof(T).Name} header.");
        }
        catch (MessagePackSerializationException e)
        {
            throw new ProtocolStatusException(NtStatus.ProtocolError, $"Malformed {typeof(T).Name} header.", inner: e);
        }
    }
}
