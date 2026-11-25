using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Integration.Api.Implements;

public sealed partial class RelayApiImplement
{
    private sealed class TunnelControllerImplement : IRelayTunnelController
    {
        private const string BaseUrl = "api/tunnel";
        private readonly RelayApiImplement _api;

        public TunnelControllerImplement(RelayApiImplement api) => _api = api;

        public Task<TunnelSessionVM> OpenSessionAsync(OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default)
            => _api.SendAsync<TunnelSessionVM>(HttpMethod.Post, $"{BaseUrl}/sessions", request, cancellationToken);
    }
}
