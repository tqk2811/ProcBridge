namespace ProcBridge.Relay.ViewModels.Devices;

public sealed record RegisterDeviceRequestVM
{
    public required string Name { get; init; }

    /// <summary>Long-term public identity key of the machine (base64), relayed to peers for fingerprint checks.</summary>
    public string? PublicKey { get; init; }
}
