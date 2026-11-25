namespace ProcBridge.Tests.Native.Models;

/// <summary>Exit code and captured output of a finished process.</summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
