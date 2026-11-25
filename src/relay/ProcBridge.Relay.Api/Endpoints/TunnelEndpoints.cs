using Microsoft.Extensions.Options;

using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Shared.Tunnel;

namespace ProcBridge.Relay.Api.Endpoints;

/// <summary>WebSocket endpoints of the tunnel: control channel of hosts, and data lanes. Both anonymous at the HTTP level.</summary>
public static class TunnelEndpoints
{
    public static IEndpointRouteBuilder MapTunnelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The host proves its HostId with a Noise handshake inside the WebSocket (see ITunnelHub.RunControlChannelAsync).
        endpoints.MapGet(TunnelProtocol.ControlPath, RunControlAsync).RequireRateLimiting(RelayRateLimitOptions.ControlPolicyName);
        // The session ticket (Authorization: Bearer pbt_...) authorizes the lane.
        endpoints.MapGet(TunnelProtocol.DataPath, RunLaneAsync).RequireRateLimiting(RelayRateLimitOptions.LanePolicyName);
        return endpoints;
    }

    private static async Task RunControlAsync(HttpContext context, ITunnelHub hub)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            throw ApiErrorException.BadRequest(RelayErrors.ValidationFailed, "Cần nâng cấp WebSocket.");
        var address = context.ClientAddress();
        // Refuse with a readable HTTP error before the upgrade; the hub checks again atomically after it.
        if (!hub.CanOpenControlChannel(address))
            throw ApiErrorException.TooManyRequests(RelayErrors.TooManyControlChannels, "Địa chỉ này đang mở quá nhiều kênh điều khiển.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await hub.RunControlChannelAsync(socket, address, context.RequestAborted);
    }

    private static async Task RunLaneAsync(HttpContext context, ITunnelHub hub, IOptions<TunnelOptions> options)
    {
        var query = context.Request.Query;
        if (!Guid.TryParse(query[TunnelProtocol.SessionIdQuery], out var sessionId)
            || !int.TryParse(query[TunnelProtocol.LaneQuery], out var lane))
            throw ApiErrorException.BadRequest(RelayErrors.ValidationFailed, "Thiếu hoặc sai sessionId/lane.");
        var ticket = ReadTicket(context, options.Value.AllowTicketInQuery);

        // Validate and reserve BEFORE accepting, so refusals are plain HTTP errors (401/404/409) the peer can read.
        var reservation = hub.ReserveLane(sessionId, ticket, lane, context.ClientAddress());
        if (!context.WebSockets.IsWebSocketRequest)
        {
            hub.ReleaseLane(reservation);
            throw ApiErrorException.BadRequest(RelayErrors.ValidationFailed, "Cần nâng cấp WebSocket.");
        }

        System.Net.WebSockets.WebSocket socket;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
        }
        catch
        {
            hub.ReleaseLane(reservation);
            throw;
        }
        using (socket)
            await hub.RunLaneAsync(reservation, socket, context.RequestAborted);
    }

    /// <summary>The ticket from <c>Authorization: Bearer pbt_...</c>; the URL only when <paramref name="allowQuery"/> (old peers).</summary>
    private static string ReadTicket(HttpContext context, bool allowQuery)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        return allowQuery ? context.Request.Query[TunnelProtocol.TicketQuery].ToString() : string.Empty;
    }
}
