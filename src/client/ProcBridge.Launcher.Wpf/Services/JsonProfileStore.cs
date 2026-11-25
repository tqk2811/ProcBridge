using System.Text.Json;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;

namespace ProcBridge.Launcher.Wpf.Services;

/// <summary>Profiles as JSON (<c>profiles.json</c> in the Launcher data folder); nothing secret is stored there.</summary>
public sealed class JsonProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonProfileStore(string path)
    {
        _path = path;
    }

    public IReadOnlyList<LaunchProfile> Load()
    {
        if (!File.Exists(_path))
            return [];
        using FileStream stream = File.OpenRead(_path);
        return JsonSerializer.Deserialize<List<LaunchProfile>>(stream, Options) ?? [];
    }

    /// <summary>Writes a temporary file then replaces, so a crash never leaves a half written list.</summary>
    public void Save(IReadOnlyList<LaunchProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, Options));
        File.Move(temporary, _path, overwrite: true);
    }
}
