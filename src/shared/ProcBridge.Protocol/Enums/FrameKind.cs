namespace ProcBridge.Protocol.Enums;

/// <summary>Role of a frame inside the request/response multiplexing.</summary>
public enum FrameKind : byte
{
    Request = 1,
    Response = 2,
    /// <summary>Response carrying an <see cref="Models.ErrorResponse"/> header instead of the normal one.</summary>
    Error = 3,
}
