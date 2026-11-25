namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Typed client of the relay REST API. Each server controller is one property. The relay has no accounts: calls are anonymous.</summary>
public interface IRelayApi
{
    Uri? BaseAddress { get; }
    IRelayTunnelController Tunnel { get; }
}
