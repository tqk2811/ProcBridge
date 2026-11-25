namespace ProcBridge.Protocol;

/// <summary>Protocol constants.</summary>
public static class ProtocolInfo
{
    /// <summary>Wire protocol version spoken by this build; checked in the Hello exchange.</summary>
    public const int Version = 3;

    /// <summary>Default TCP port of the Agent on the LAN.</summary>
    public const int DefaultPort = 47800;
}
