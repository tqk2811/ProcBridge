using ProcBridge.Broker.Models;
using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Transport.Relay;
using ProcBridge.Protocol.Enums;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.Services;

/// <summary>
/// Turns a <see cref="LaunchProfile"/> into the <see cref="LauncherOptions"/> the console Launcher would build from
/// the same command line (same quoting, same Spawn lookup). Security is added by the runner.
/// </summary>
public sealed class LaunchOptionsFactory
{
    private readonly string _spawnDirectory;

    /// <param name="spawnDirectory">Folder holding <c>ProcBridge.Spawn.&lt;arch&gt;.exe</c> (the Launcher's folder by default).</param>
    public LaunchOptionsFactory(string spawnDirectory)
    {
        _spawnDirectory = spawnDirectory;
    }

    /// <exception cref="ProfileValidationException">Something is missing or wrong.</exception>
    public LauncherOptions Create(LaunchProfile profile, PairedMachine? machine)
    {
        var errors = new List<string>();
        if (machine is null)
            errors.Add("Chưa chọn máy B đã ghép cặp.");
        if (string.IsNullOrWhiteSpace(profile.ShareName))
            errors.Add("Chưa nhập tên chia sẻ (share) của máy B.");
        if (string.IsNullOrWhiteSpace(profile.ShareRoot))
            errors.Add("Chưa chọn thư mục gốc của chia sẻ trên máy B.");
        if (profile.Mappings.Count == 0)
            errors.Add("Cần ít nhất một ánh xạ thư mục.");
        if (string.IsNullOrWhiteSpace(profile.Program))
            errors.Add("Chưa nhập chương trình cần chạy.");
        if (profile.Architecture is not ("x64" or "x86"))
            errors.Add("Kiến trúc phải là x64 hoặc x86.");
        // HostId comes from the paired machine (its fingerprint); only the relay URL is asked for. A machine paired through
        // the relay has no LAN address: its relay is used when the profile gives none.
        LauncherRelay? relay = null;
        Uri? machineRelay = RelayPairingConnector.TryParseRelayAddress(machine?.Address);
        if (profile.UseRelay && Uri.TryCreate(profile.RelayUrl.Trim(), UriKind.Absolute, out Uri? relayUrl) && relayUrl.Scheme is "https" or "http")
            relay = new LauncherRelay(relayUrl);
        else if (machineRelay is not null && (!profile.UseRelay || string.IsNullOrWhiteSpace(profile.RelayUrl)))
            relay = new LauncherRelay(machineRelay);
        else if (profile.UseRelay)
            errors.Add("Địa chỉ relay phải là URL http(s), vd https://relay.example.com/.");

        var mappings = new List<VirtualMapping>();
        foreach (FolderMapping mapping in profile.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.VirtualRoot) || !Path.IsPathFullyQualified(mapping.VirtualRoot))
            {
                errors.Add($"Thư mục ảo '{mapping.VirtualRoot}' phải là đường dẫn tuyệt đối trên máy này.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(profile.ShareRoot))
                continue;
            string? relative = RemotePathHelper.ToShareRelative(profile.ShareRoot, mapping.RemotePath);
            if (relative is null)
            {
                errors.Add($"Thư mục '{mapping.RemotePath}' không nằm trong thư mục chia sẻ '{profile.ShareRoot}' của máy B.");
                continue;
            }
            mappings.Add(new VirtualMapping(Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.VirtualRoot)), relative));
        }
        if (errors.Count > 0 || machine is null)
            throw new ProfileValidationException(errors);

        // Through the relay the LAN address is not used (the paired machine is picked by fingerprint).
        (string host, int port) = relay is not null
            ? (relay.Url.Host, Protocol.ProtocolInfo.DefaultPort)
            : LauncherCommandLine.ParseAgent(machine.Address);
        string commandLine = LauncherCommandLine.Quote(profile.Program.Trim());
        if (!string.IsNullOrWhiteSpace(profile.Arguments))
            commandLine += " " + profile.Arguments.Trim();
        return new LauncherOptions
        {
            AgentHost = host,
            AgentPort = port,
            AgentFingerprint = machine.Fingerprint,
            ShareName = profile.ShareName.Trim(),
            Mappings = mappings,
            SpawnPath = Path.Combine(_spawnDirectory, $"ProcBridge.Spawn.{profile.Architecture}.exe"),
            DisableBuildServers = profile.DisableBuildServers,
            WriteThrough = profile.WriteThrough,
            ConflictPolicy = profile.OverwriteOnConflict ? ConflictPolicy.Overwrite : ConflictPolicy.KeepBoth,
            Relay = relay,
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? null : profile.WorkingDirectory.Trim(),
            CommandLine = commandLine,
        };
    }
}
