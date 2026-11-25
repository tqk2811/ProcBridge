namespace ProcBridge.Protocol.Interfaces;

/// <summary>Opens new frame channels to one Agent. TCP in M2; the relay tunnel (M6) is another implementation.</summary>
public interface IConnectionFactory
{
    /// <summary>Human readable target, for logs and errors.</summary>
    string Description { get; }

    ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken);
}
