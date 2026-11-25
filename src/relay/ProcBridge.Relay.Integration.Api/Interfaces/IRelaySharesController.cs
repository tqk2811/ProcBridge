using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Group <c>api/shares</c> (account token).</summary>
public interface IRelaySharesController
{
    /// <summary><c>POST api/shares</c>.</summary>
    Task<ShareVM> CreateAsync(CreateShareRequestVM request, CancellationToken cancellationToken = default);

    /// <summary><c>GET api/shares</c>: shares served by the caller's devices.</summary>
    Task<IReadOnlyList<ShareVM>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary><c>DELETE api/shares/{shareId}</c>.</summary>
    Task DeleteAsync(Guid shareId, CancellationToken cancellationToken = default);

    /// <summary><c>POST api/shares/{shareId}/grants</c>: allow a device (any account) to use the share.</summary>
    Task<ShareGrantVM> GrantAsync(Guid shareId, GrantShareRequestVM request, CancellationToken cancellationToken = default);

    /// <summary><c>GET api/shares/{shareId}/grants</c>.</summary>
    Task<IReadOnlyList<ShareGrantVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE api/shares/{shareId}/grants/{deviceId}</c>.</summary>
    Task RevokeGrantAsync(Guid shareId, Guid deviceId, CancellationToken cancellationToken = default);
}
