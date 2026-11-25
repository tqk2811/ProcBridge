using ProcBridge.Agent.Wpf.Core.Interfaces;

namespace ProcBridge.Agent.Wpf.Tests.Fakes;

internal sealed class FakePairingPrompt : IPairingPrompt
{
    public const string Fingerprint = "ABCD-EFGH-IJKL-MNOP-QRST-UVWX-YZ23-4567";

    public TaskCompletionSource<bool> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Code => "123456";

    public string PeerFingerprint => Fingerprint;

    public string PeerName => "machine-a";

    public string RemoteAddress => "10.0.0.2:50000";

    public Task<bool> Completion => CompletionSource.Task;

    public int AcceptCalls { get; private set; }

    public int RejectCalls { get; private set; }

    public bool Accept()
    {
        AcceptCalls++;
        return true;
    }

    public bool Reject()
    {
        RejectCalls++;
        return true;
    }
}
