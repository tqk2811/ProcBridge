namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>A machine connected right now (its pool of connections grouped), or one connection still in the handshake.</summary>
public sealed record ConnectedMachineViewModel
{
    public required string Name { get; init; }

    /// <summary>Empty while the handshake has not authenticated the peer.</summary>
    public required string Fingerprint { get; init; }

    public required string RemoteAddresses { get; init; }

    public required int ConnectionCount { get; init; }

    public required DateTime ConnectedSinceLocal { get; init; }
}
