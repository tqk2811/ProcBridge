namespace ProcBridge.Relay.Cli;

/// <summary>Wrong command line: print the message and the usage, exit code 2.</summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}
