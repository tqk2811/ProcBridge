namespace ProcBridge.Relay.Services.Tunnel.Enums;

/// <summary>Which end of a session a lane belongs to.</summary>
public enum TunnelSide
{
    /// <summary>Machine A (Broker): opened the session through REST.</summary>
    Client,

    /// <summary>Machine B (Agent): serves the share, learned about the session on its control channel.</summary>
    Host,
}
