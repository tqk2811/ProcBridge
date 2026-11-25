using Newtonsoft.Json;

namespace ProcBridge.Relay.Cli;

/// <summary>
/// JSON file holding the relay URL and the account session token of the last <c>login</c>. The token is a bearer
/// secret: the file lives in the user profile, and <c>logout</c> clears it.
/// </summary>
public sealed class CliConfigStore
{
    private readonly string _path;

    public CliConfigStore(string path) => _path = path;

    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProcBridge", "relay-cli.json");

    public CliConfig Load()
    {
        if (!File.Exists(_path))
            return new CliConfig();
        return JsonConvert.DeserializeObject<CliConfig>(File.ReadAllText(_path)) ?? new CliConfig();
    }

    public void Save(CliConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonConvert.SerializeObject(config, Formatting.Indented));
    }
}
