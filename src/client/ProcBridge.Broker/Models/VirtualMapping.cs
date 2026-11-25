namespace ProcBridge.Broker.Models;

/// <summary>One virtual folder of the session (plan 4.3).</summary>
/// <param name="VirtualRoot">DOS path on machine A, need not exist (<c>D:\Remote\MyProject</c>).</param>
/// <param name="RemotePath">Share-relative folder on machine B, '/' separated (<c>src/MyProject</c>, empty for the share root).</param>
public sealed record VirtualMapping(string VirtualRoot, string RemotePath);
