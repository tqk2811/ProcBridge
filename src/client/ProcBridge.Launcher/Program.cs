using System.Runtime.Versioning;
using ProcBridge.Launcher.Models;

namespace ProcBridge.Launcher;

/// <summary>Console entry point, see <see cref="LauncherCommandLine.Usage"/>.</summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        LauncherOptions options;
        try
        {
            options = LauncherCommandLine.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(LauncherCommandLine.Usage);
            return 2;
        }

        // Ctrl+C reaches the target (same console); the Launcher keeps the Broker up until the target exits.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        try
        {
            return await new LaunchSession(options).RunAsync();
        }
        catch (FileNotFoundException e)
        {
            Console.Error.WriteLine($"ProcBridge.Launcher: {e.Message} {e.FileName}");
            return 2;
        }
    }
}
