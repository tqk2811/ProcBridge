using ProcBridge.Relay.Services.Common.Security;

namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Commands the operator runs instead of serving: <c>ProcBridge.Relay.Api account hash</c> prints the password hash to
/// put in <c>Relay:Accounts:Seed</c> (the relay stores no data on disk, so there is no "create account" command any
/// more). The password comes from <see cref="PasswordEnvironmentVariable"/> or is read from the console.
/// </summary>
public static class OperatorCommands
{
    public const string PasswordEnvironmentVariable = "PROCBRIDGE_RELAY_PASSWORD";

    private const int MinPasswordLength = 8;

    /// <summary>True when <paramref name="args"/> start with an operator command (then the host must not serve).</summary>
    public static bool IsOperatorCommand(string[] args) => args.Length >= 1 && args[0] == "account";

    /// <summary>Splits the operator command from the remaining host arguments (<c>--key=value</c> configuration).</summary>
    public static (string[] Command, string[] HostArgs) Split(string[] args)
    {
        var command = args.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
        return (command, args.Skip(command.Length).ToArray());
    }

    /// <returns>Process exit code: 0 done, 1 refused, 2 usage.</returns>
    public static async Task<int> RunAsync(string[] command, TextWriter output, TextWriter error)
    {
        if (command.Length != 2 || command[0] != "account" || command[1] != "hash")
        {
            await error.WriteLineAsync("Usage: ProcBridge.Relay.Api account hash");
            await error.WriteLineAsync($"  Password: environment variable {PasswordEnvironmentVariable}, or typed on the console.");
            await error.WriteLineAsync("  Put the printed hash in Relay:Accounts:Seed (UserName + PasswordHash) of the relay configuration.");
            return 2;
        }
        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            await output.WriteAsync("Password: ");
            password = ReadHidden();
        }
        if (password.Length < MinPasswordLength)
        {
            await error.WriteLineAsync($"Password must have at least {MinPasswordLength} characters.");
            return 1;
        }
        await output.WriteLineAsync(new PasswordHasher().Hash(password));
        return 0;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                    text.Length--;
                continue;
            }
            text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
