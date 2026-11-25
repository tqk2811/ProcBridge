namespace ProcBridge.Launcher.Wpf.Helpers;

/// <summary>Converts Remote Explorer paths (absolute, on machine B) to share relative paths of the Agent protocol.</summary>
public static class RemotePathHelper
{
    /// <summary>
    /// <c>D:\src</c> + <c>D:\src\app\lib</c> gives <c>app/lib</c>; the root itself gives an empty string. Null when
    /// <paramref name="path"/> is not inside <paramref name="shareRoot"/> (case-insensitive, Windows paths).
    /// </summary>
    public static string? ToShareRelative(string shareRoot, string path)
    {
        string root = Normalize(shareRoot);
        string full = Normalize(path);
        if (root.Length == 0 || full.Length == 0)
            return null;
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return full[prefix.Length..].Replace('\\', '/');
    }

    private static string Normalize(string path)
    {
        string trimmed = path.Trim().Replace('/', '\\');
        // Keep "D:\" whole; drop the trailing separator of anything longer.
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\') : trimmed;
    }
}
