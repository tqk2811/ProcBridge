namespace ProcBridge.Transport.Secure;

/// <summary>
/// A machine A asks to pair (raised on machine B by <see cref="SecureServer.PairingRequested"/>). The UI shows
/// <see cref="Code"/> for the user to type on A and <see cref="PeerFingerprint"/> to compare with what A shows, then calls
/// <see cref="Accept"/> or <see cref="Reject"/>. The decision may come before A typed the code; it only takes effect once
/// A proved it typed the right code, so accepting early never trusts a machine that got the code wrong.
/// </summary>
public sealed class PairingRequest
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PairingRequest(string code, string peerFingerprint, string remoteAddress, string peerName)
    {
        PeerName = peerName;
        Code = code;
        PeerFingerprint = peerFingerprint;
        RemoteAddress = remoteAddress;
    }

    /// <summary>6-digit code to display on machine B.</summary>
    public string Code { get; }

    /// <summary>Fingerprint of machine A's identity key; machine A shows the same value.</summary>
    public string PeerFingerprint { get; }

    /// <summary>Machine name A sent (not authenticated before the user accepts).</summary>
    public string PeerName { get; }

    /// <summary>Network address the request came from, for display only (not authenticated).</summary>
    public string RemoteAddress { get; }

    /// <summary>True when machine A was stored as paired, false when the pairing failed, was rejected or timed out.</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>The user on B approves machine A. Returns false when a decision was already made.</summary>
    public bool Accept() => _decision.TrySetResult(true);

    /// <summary>The user on B refuses machine A. Returns false when a decision was already made.</summary>
    public bool Reject() => _decision.TrySetResult(false);

    internal Task<bool> Decision => _decision.Task;

    internal void Complete(bool paired)
    {
        _decision.TrySetResult(false);
        _completion.TrySetResult(paired);
    }
}
