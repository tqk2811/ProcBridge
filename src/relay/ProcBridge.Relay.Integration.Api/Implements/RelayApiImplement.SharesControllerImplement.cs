using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Integration.Api.Implements;

public sealed partial class RelayApiImplement
{
    private sealed class SharesControllerImplement : IRelaySharesController
    {
        private const string BaseUrl = "api/shares";
        private readonly RelayApiImplement _api;

        public SharesControllerImplement(RelayApiImplement api) => _api = api;

        public Task<ShareVM> CreateAsync(CreateShareRequestVM request, CancellationToken cancellationToken = default)
            => _api.SendAsync<ShareVM>(HttpMethod.Post, BaseUrl, request, cancellationToken);

        public async Task<IReadOnlyList<ShareVM>> GetAllAsync(CancellationToken cancellationToken = default)
            => await _api.SendAsync<List<ShareVM>>(HttpMethod.Get, BaseUrl, null, cancellationToken);

        public Task DeleteAsync(Guid shareId, CancellationToken cancellationToken = default)
            => _api.SendVoidAsync(HttpMethod.Delete, $"{BaseUrl}/{shareId}", null, cancellationToken);

        public Task<ShareGrantVM> GrantAsync(Guid shareId, GrantShareRequestVM request, CancellationToken cancellationToken = default)
            => _api.SendAsync<ShareGrantVM>(HttpMethod.Post, $"{BaseUrl}/{shareId}/grants", request, cancellationToken);

        public async Task<IReadOnlyList<ShareGrantVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default)
            => await _api.SendAsync<List<ShareGrantVM>>(HttpMethod.Get, $"{BaseUrl}/{shareId}/grants", null, cancellationToken);

        public Task RevokeGrantAsync(Guid shareId, Guid deviceId, CancellationToken cancellationToken = default)
            => _api.SendVoidAsync(HttpMethod.Delete, $"{BaseUrl}/{shareId}/grants/{deviceId}", null, cancellationToken);
    }
}
