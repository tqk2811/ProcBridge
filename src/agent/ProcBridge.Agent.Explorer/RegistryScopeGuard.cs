using Microsoft.Win32;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer;

/// <summary>
/// Parses registry key paths and checks them against the registry roots of the policy.
/// Segments that are empty, <c>.</c> or <c>..</c> are rejected so a path always means what it reads.
/// </summary>
internal sealed class RegistryScopeGuard
{
    private static readonly Dictionary<string, (string Short, RegistryHive Hive)> Hives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HKLM"] = ("HKLM", RegistryHive.LocalMachine),
        ["HKEY_LOCAL_MACHINE"] = ("HKLM", RegistryHive.LocalMachine),
        ["HKCU"] = ("HKCU", RegistryHive.CurrentUser),
        ["HKEY_CURRENT_USER"] = ("HKCU", RegistryHive.CurrentUser),
        ["HKCR"] = ("HKCR", RegistryHive.ClassesRoot),
        ["HKEY_CLASSES_ROOT"] = ("HKCR", RegistryHive.ClassesRoot),
        ["HKU"] = ("HKU", RegistryHive.Users),
        ["HKEY_USERS"] = ("HKU", RegistryHive.Users),
        ["HKCC"] = ("HKCC", RegistryHive.CurrentConfig),
        ["HKEY_CURRENT_CONFIG"] = ("HKCC", RegistryHive.CurrentConfig),
    };

    private readonly string[] _roots;

    public RegistryScopeGuard(IEnumerable<string> roots)
    {
        _roots = [.. roots.Select(r => Parse(r, throwArgument: true).Normalized)];
    }

    /// <summary>Parses and checks a key path. <paramref name="forChange"/> also forbids changing a root itself.</summary>
    public RegistryPath Resolve(string? path, bool forChange = false)
    {
        var parsed = Parse(path, throwArgument: false);
        if (!_roots.Any(r => IsUnder(parsed.Normalized, r)))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "Key is outside the allowed registry roots.");
        if (forChange && _roots.Any(r => string.Equals(r, parsed.Normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "An allowed root key itself cannot be deleted.");
        return parsed;
    }

    private static RegistryPath Parse(string? path, bool throwArgument)
    {
        void Fail(string message)
        {
            if (throwArgument)
                throw new ArgumentException(message, nameof(path));
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, message);
        }

        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            Fail("Registry path is empty.");
        var segments = path!.Trim().TrimEnd('\\').Split('\\');
        if (!Hives.TryGetValue(segments[0], out var hive))
            Fail($"Unknown registry hive '{segments[0]}'.");
        var sub = segments.Skip(1).ToArray();
        if (sub.Any(s => s.Length == 0 || s == "." || s == ".."))
            Fail("Registry path has an empty, '.' or '..' segment.");

        var subKey = string.Join('\\', sub);
        var normalized = sub.Length == 0 ? hive.Short : hive.Short + "\\" + subKey;
        return new RegistryPath(hive.Hive, subKey, normalized);
    }

    private static bool IsUnder(string path, string root)
        => (path + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
