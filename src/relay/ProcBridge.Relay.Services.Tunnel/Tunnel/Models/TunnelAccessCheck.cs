namespace ProcBridge.Relay.Services.Tunnel.Models;

/// <summary>
/// Closes the gap between "the caller checked the permission in the database" and "the hub registered the session or
/// control channel": <see cref="Epoch"/> is <c>ITunnelHub.RevocationEpoch</c> read BEFORE that check. When the epoch
/// moved by the time the hub has registered the entry, a revocation may have run in between, so the hub asks
/// <see cref="StillAllowed"/> (a fresh database check) and drops the entry when it says no.
/// </summary>
/// <param name="Epoch">Revocation epoch captured before the permission check.</param>
/// <param name="StillAllowed">Checks the permission again; false when it was revoked.</param>
public sealed record TunnelAccessCheck(long Epoch, Func<CancellationToken, Task<bool>> StillAllowed);
