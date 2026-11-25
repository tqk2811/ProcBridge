namespace ProcBridge.Relay.Cli;

/// <summary>
/// Minimal argument parser: positional words plus <c>--name value</c> options (and <c>--flag</c> without value when it is
/// the last word or followed by another option).
/// </summary>
public sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = new();

    public CliArguments(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                string? value = null;
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = args[++i];
                _options[name] = value;
            }
            else
            {
                _positionals.Add(arg);
            }
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string? Option(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public bool HasOption(string name) => _options.ContainsKey(name);

    /// <summary>Positional word <paramref name="index"/>, or a usage error naming <paramref name="what"/>.</summary>
    public string Required(int index, string what)
        => index < _positionals.Count ? _positionals[index] : throw new CliUsageException($"Thiếu {what}.");

    public Guid RequiredGuid(int index, string what)
        => Guid.TryParse(Required(index, what), out var id) ? id : throw new CliUsageException($"{what} phải là GUID.");
}
