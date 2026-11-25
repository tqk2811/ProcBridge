using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Devices.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.Shared.Tunnel;

namespace ProcBridge.Relay.Api.Endpoints;

/// <summary>WebSocket endpoints of the tunnel: control channel of host devices, and data lanes.</summary>
public static class TunnelEndpoints
{
    public static IEndpointRouteBuilder MapTunnelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(TunnelProtocol.ControlPath, RunControlAsync).RequireAuthorization(RelayAuth.DevicePolicy);
        // Anonymous at the HTTP level: the session ticket in the query authorizes the lane.
        endpoints.MapGet(TunnelProtocol.DataPath, RunLaneAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task RunControlAsync(HttpContext context, ITunnelHub hub, IDeviceService devices)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            throw ApiErrorException.BadRequest(RelayErrors.ValidationFailed, "Cần nâng cấp WebSocket.");
        var deviceId = context.User.GetDeviceId();
        // Epoch read by the authentication handler before it checked the token; absent means "always re-check".
        var epoch = context.Items[AuthItemKeys.RevocationEpoch] is long captured ? captured : -1;
        var access = new TunnelAccessCheck(epoch, ct => devices.IsActiveAsync(deviceId, ct));
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await hub.RunControlChannelAsync(deviceId, socket, context.RequestAborted, access);
    }

    private static async Task RunLaneAsync(HttpContext context, ITunnelHub hub)
    {
        var query = context.Request.Query;
        if (!Guid.TryParse(query[TunnelProtocol.SessionIdQuery], out var sessionId)
            || !int.TryParse(query[TunnelProtocol.LaneQuery], out var lane))
            throw ApiErrorException.BadRequest(RelayErrors.ValidationFailed, "Thiếu hoặc sai sessionId/lane.");
        var ticket = query[TunnelProtocol.TicketQuery].ToString();

        // Validate and reserve BEFORE accepting, so refusals are plain HTTP errors (401/404/409) the peer can read.
        var reservation = hub.ReserveLane(sessionId, ticket, lane);
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
}
