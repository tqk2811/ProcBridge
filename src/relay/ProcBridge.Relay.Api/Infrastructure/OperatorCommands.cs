using ProcBridge.Relay.Services.Accounts.Interfaces;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Commands the operator runs on the relay host itself, against the relay database, instead of serving:
/// <c>ProcBridge.Relay.Api account create &lt;user&gt;</c> creates an account even when self-registration is off
/// (the default). The password comes from <see cref="PasswordEnvironmentVariable"/> or is read from the console.
/// </summary>
public static class OperatorCommands
{
    public const string PasswordEnvironmentVariable = "PROCBRIDGE_RELAY_PASSWORD";

    /// <summary>True when <paramref name="args"/> start with an operator command (then the host must not serve).</summary>
    public static bool IsOperatorCommand(string[] args) => args.Length >= 1 && args[0] == "account";

    /// <summary>Splits the operator command from the remaining host arguments (<c>--key=value</c> configuration).</summary>
    public static (string[] Command, string[] HostArgs) Split(string[] args)
    {
        var command = args.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray();
        return (command, args.Skip(command.Length).ToArray());
    }

    /// <returns>Process exit code: 0 done, 1 refused, 2 usage.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string[] command, TextWriter output, TextWriter error)
    {
        if (command.Length != 3 || command[0] != "account" || command[1] != "create")
        {
            await error.WriteLineAsync("Usage: ProcBridge.Relay.Api account create <user> [--ConnectionStrings:Relay=...]");
            await error.WriteLineAsync($"  Password: environment variable {PasswordEnvironmentVariable}, or typed on the console.");
            return 2;
        }
        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            await output.WriteAsync("Password: ");
            password = ReadHidden();
        }

        await using var scope = services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        try
        {
            var created = await accounts.CreateByOperatorAsync(new RegisterAccountRequestVM { UserName = command[2], Password = password });
            await output.WriteLineAsync($"Created account {created.UserName} ({created.Id}).");
            return 0;
        }
        catch (ServiceException ex)
        {
            await error.WriteLineAsync($"{ex.Code}: {ex.Message}");
            return 1;
        }
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
