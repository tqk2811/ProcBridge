namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <summary>
/// First text message on <c>/tunnel/control</c>, after the Noise handshake proved the host owns its identity key: the
/// HostId the relay registered the channel under (the fingerprint of that key).
/// </summary>
public sealed record TunnelHostRegisteredMessageVM(string Type, string HostId);
