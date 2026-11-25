namespace ProcBridge.Transport.Secure.Interfaces;

/// <summary>Opens raw byte streams to one Agent (TCP on the LAN; the relay tunnel of M6 is another implementation).</summary>
public interface IStreamConnector
{
    /// <summary>Human readable target (<c>host:port</c>), for logs, errors and the paired machine address.</summary>
    string Description { get; }

    /// <summary>Opens a new stream; the caller owns it.</summary>
    ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken);
}
