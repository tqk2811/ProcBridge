namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Typed client of the relay REST API. Each server controller is one property.</summary>
public interface IRelayApi
{
    Uri? BaseAddress { get; }
    IRelayAccountsController Accounts { get; }
    IRelayDevicesController Devices { get; }
    IRelaySharesController Shares { get; }
    IRelayTunnelController Tunnel { get; }

    /// <summary>Account session token or device token sent as <c>Authorization: Bearer</c>; <c>null</c> = anonymous.</summary>
    void SetBearerToken(string? token);
}
