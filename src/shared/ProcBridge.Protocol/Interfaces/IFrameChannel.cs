using ProcBridge.Protocol.Models;

namespace ProcBridge.Protocol.Interfaces;

/// <summary>A bidirectional stream of frames (one TCP connection, later one relay tunnel).</summary>
public interface IFrameChannel : IAsyncDisposable
{
    /// <summary>Sends one frame. Safe to call from several threads at once.</summary>
    ValueTask SendAsync(Frame frame, CancellationToken cancellationToken);

    /// <summary>Receives the next frame, or null when the peer closed the connection cleanly. Single reader only.</summary>
    ValueTask<Frame?> ReceiveAsync(CancellationToken cancellationToken);
}
