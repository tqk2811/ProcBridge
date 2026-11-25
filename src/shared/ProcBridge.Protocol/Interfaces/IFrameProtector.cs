namespace ProcBridge.Protocol.Interfaces;

/// <summary>
/// Plug-in point for the end-to-end encryption layer (M5, plan section 9). It transforms each frame body after
/// framing, so the transport (TCP or relay WebSocket) only ever sees <c>u32 length | protected body</c>.
/// Implementations keep their own nonce/sequence state; the channel calls <see cref="Protect"/> under its send
/// lock and <see cref="Unprotect"/> from its single reader, in wire order.
/// </summary>
public interface IFrameProtector
{
    /// <summary>Maximum number of bytes <see cref="Protect"/> adds to a body (tag, sequence number...).</summary>
    int Overhead { get; }

    ReadOnlyMemory<byte> Protect(ReadOnlyMemory<byte> body);

    /// <summary>Returns the plain body; throws when authentication fails.</summary>
    ReadOnlyMemory<byte> Unprotect(ReadOnlyMemory<byte> data);
}
