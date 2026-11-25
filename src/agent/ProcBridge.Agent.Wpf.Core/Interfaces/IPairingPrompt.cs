namespace ProcBridge.Agent.Wpf.Core.Interfaces;

/// <summary>A pairing request as the UI sees it (wraps <c>PairingRequest</c>, whose constructor is internal).</summary>
public interface IPairingPrompt
{
    /// <summary>6-digit code the user types on machine A.</summary>
    string Code { get; }

    /// <summary>Fingerprint of machine A (8 groups of 4 base32 characters joined by '-').</summary>
    string PeerFingerprint { get; }

    /// <summary>Name machine A sent, not authenticated.</summary>
    string PeerName { get; }

    /// <summary>Address the request came from, not authenticated.</summary>
    string RemoteAddress { get; }

    /// <summary>True once machine A is stored as paired; false when it failed, was rejected or timed out.</summary>
    Task<bool> Completion { get; }

    bool Accept();

    bool Reject();
}
