using System.Text;

using ProcBridge.Relay.Cli.Interfaces;

namespace ProcBridge.Relay.Cli;

/// <summary>Reads a password without echo on a console; reads a plain line when stdin is redirected.</summary>
public sealed class ConsolePasswordReader : IPasswordReader
{
    public string Read(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                    builder.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                builder.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return builder.ToString();
    }
}
