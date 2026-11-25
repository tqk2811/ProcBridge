using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent.Wpf.Core;

/// <summary>Adapts a transport <see cref="PairingRequest"/> to <see cref="IPairingPrompt"/>.</summary>
public sealed class PairingRequestPrompt(PairingRequest request) : IPairingPrompt
{
    public string Code => request.Code;

    public string PeerFingerprint => request.PeerFingerprint;

    public string PeerName => request.PeerName;

    public string RemoteAddress => request.RemoteAddress;

    public Task<bool> Completion => request.Completion;

    public bool Accept() => request.Accept();

    public bool Reject() => request.Reject();
}
