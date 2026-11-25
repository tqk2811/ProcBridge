namespace ProcBridge.Transport.Secure.Models;

public sealed record SecureServerOptions
{
    /// <summary>Time a new connection has to finish its preamble and handshake.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Accept pairing requests (first-time XX handshake). Off unless the user opened pairing on machine B.</summary>
    public bool AllowPairing { get; init; }

    /// <summary>Time the users have to compare the code and decide, from the XX handshake to the decision.</summary>
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Largest frame body, as in <c>TransportOptions.MaxFrameSize</c>.</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024 + 64 * 1024;
}
