using System.Text.Json;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Models;

namespace ProcBridge.Agent.Wpf.Core;

/// <summary>Settings of the app in a JSON file, written atomically (temp file then replace).</summary>
public sealed class JsonAgentSettingsStore(string path) : IAgentSettingsStore
{
    /// <summary>File name inside the Agent data folder.</summary>
    public const string FileName = "agent-ui.json";

    private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public AgentUiSettings Load()
    {
        if (!File.Exists(path))
            return new AgentUiSettings();
        try
        {
            AgentUiSettings settings = JsonSerializer.Deserialize<AgentUiSettings>(File.ReadAllText(path), _options) ?? new AgentUiSettings();
            // The deserialized dictionary uses the default comparer; fingerprints are compared exactly anyway.
            settings.LastSeenUtc = new Dictionary<string, DateTime>(settings.LastSeenUtc ?? [], StringComparer.Ordinal);
            settings.Shares ??= [];
            return settings;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Settings file '{path}' is corrupted.", e);
        }
    }

    public void Save(AgentUiSettings settings)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, _options));
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }
}
