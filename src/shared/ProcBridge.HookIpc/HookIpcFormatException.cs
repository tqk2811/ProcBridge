namespace ProcBridge.HookIpc;

/// <summary>A message is truncated, oversized or otherwise does not follow the IPC format.</summary>
public sealed class HookIpcFormatException(string message) : Exception(message);
