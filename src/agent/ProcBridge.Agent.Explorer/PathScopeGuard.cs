using Microsoft.Win32.SafeHandles;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Native;

namespace ProcBridge.Agent.Explorer;

/// <summary>
/// Decides whether a path sent by machine A lies inside the folders the policy allows, and hands
/// out the handle that was checked. Two checks: a lexical one on the normalized path (stops
/// <c>..</c>, prefix tricks, UNC/device paths, alternate data streams) and a physical one on the
/// final path of the opened handle (stops junctions, symbolic links and mount points that lead
/// outside every root).
/// </summary>
/// <remarks>
/// The physical check runs on the very handle the caller then uses, so a folder swapped for a link
/// after the check cannot redirect the operation (no check-then-use gap by name).
/// </remarks>
internal sealed class PathScopeGuard
{
    /// <summary>Access on a parent folder that new entries are created or moved into.</summary>
    private const uint ParentAccess = NativeMethods.FileListDirectory | NativeMethods.FileTraverse | NativeMethods.FileReadAttributes;

    /// <summary>Parent folders are opened without FILE_SHARE_DELETE so they cannot be moved while in use.</summary>
    private const uint ParentShare = NativeMethods.FileShareRead | NativeMethods.FileShareWrite;

    private readonly string[] _roots;
    private readonly string[] _finalRoots;

    public PathScopeGuard(IEnumerable<string> roots)
    {
        var lexical = new List<string>();
        var physical = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || IsUncOrDevice(root))
                throw new ArgumentException($"File root must be a local absolute path: '{root}'", nameof(roots));
            var full = TrimEnd(Path.GetFullPath(root));
            lexical.Add(full);
            physical.Add(GetFinalPath(full) ?? full);
        }

        _roots = [.. lexical];
        _finalRoots = [.. physical];
    }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Test seam: runs after a path passed the scope check and before it is used.</summary>
    internal Action<string>? AfterScopeCheck { get; set; }

    /// <summary>
    /// Opens a path for reading, following links: the object reached (after every link on the way)
    /// must lie inside a root. Returns the checked handle and the normalized path.
    /// </summary>
    public SafeFileHandle OpenForRead(string? path, uint access, out string full)
    {
        full = Normalize(path);
        var handle = NativeFile.Open(full, access, followLinks: true, NativeMethods.FileShareAll);
        return Checked(handle, full, allowRoot: true);
    }

    /// <summary>
    /// Opens the entry a path names for changing it (delete, rename source). The entry itself is
    /// opened, not the target of a link, so deleting a link removes the link only. The entry must
    /// lie strictly inside a root: a root itself cannot be changed.
    /// </summary>
    public SafeFileHandle OpenEntryForChange(string? path, uint access, out string full)
    {
        full = NormalizeForChange(path);
        var handle = NativeFile.Open(full, access, followLinks: false, NativeMethods.FileShareAll);
        return Checked(handle, full, allowRoot: false);
    }

    /// <summary>Opens the existing parent folder a new entry goes into (rename target) and returns the name of the entry.</summary>
    public SafeFileHandle OpenParentForChange(string? path, out string full, out string name)
    {
        full = NormalizeForChange(path);
        var parent = Path.GetDirectoryName(full)!;
        name = Path.GetFileName(full);
        var handle = NativeFile.Open(parent, ParentAccess, followLinks: true, ParentShare);
        return Checked(handle, full, allowRoot: true);
    }

    /// <summary>
    /// Opens the deepest existing folder on the way to a path that is to be created, and returns
    /// the names still missing below it (outermost first, the new entry last).
    /// </summary>
    public SafeFileHandle OpenAncestorForCreate(string? path, out string full, out IReadOnlyList<string> missing)
    {
        full = NormalizeForChange(path);
        var names = new List<string> { Path.GetFileName(full) };
        var current = Path.GetDirectoryName(full)!;
        while (true)
        {
            if (!_roots.Any(r => IsUnder(current, r)))
                throw new ExplorerDeniedException(ExplorerStatus.NotFound, "The allowed folder does not exist.");
            var handle = NativeFile.TryOpen(current, ParentAccess, followLinks: true, ParentShare, out var error);
            if (handle is not null)
            {
                names.Reverse();
                missing = names;
                return Checked(handle, full, allowRoot: true);
            }

            if (error is not (2 or 3))
                throw NativeFile.Error(error, current);
            names.Add(Path.GetFileName(current));
            current = Path.GetDirectoryName(current)!;
        }
    }

    private SafeFileHandle Checked(SafeFileHandle handle, string full, bool allowRoot)
    {
        try
        {
            var final = GetFinalPath(handle)
                ?? throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "Cannot resolve the real location of the path.");
            var inside = allowRoot
                ? _finalRoots.Any(r => IsUnder(final, r))
                : _finalRoots.Any(r => IsUnder(final, r) && !string.Equals(final, r, StringComparison.OrdinalIgnoreCase));
            if (!inside)
                throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "Path leads outside the allowed folders through a link.");
            AfterScopeCheck?.Invoke(full);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private string NormalizeForChange(string? path)
    {
        var full = Normalize(path);
        if (_roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "An allowed root itself cannot be changed.");
        if (Path.GetDirectoryName(full) is null)
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "A drive root cannot be changed.");
        return full;
    }

    private string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Path is empty.");
        if (IsUncOrDevice(path))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "UNC and device paths are not allowed.");
        if (!Path.IsPathFullyQualified(path))
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Path must be absolute.");
        if (path.IndexOf(':', 2) >= 0)
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Alternate data streams are not allowed.");

        var full = TrimEnd(Path.GetFullPath(path));
        if (!_roots.Any(r => IsUnder(full, r)))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "Path is outside the allowed folders.");
        return full;
    }

    private static bool IsUncOrDevice(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
        || path.StartsWith(@"\/", StringComparison.Ordinal) || path.StartsWith(@"/\", StringComparison.Ordinal);

    private static bool IsUnder(string path, string root)
    {
        var r = root.EndsWith('\\') ? root : root + '\\';
        var p = path.EndsWith('\\') ? path : path + '\\';
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEnd(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    private static string? GetFinalPath(SafeFileHandle handle)
        => NativeFile.GetFinalPath(handle) is { } final ? TrimEnd(final) : null;

    /// <summary>Final path of an existing root folder; null when it does not exist (yet).</summary>
    private static string? GetFinalPath(string path)
    {
        using var handle = NativeFile.TryOpen(path, NativeMethods.FileReadAttributes, followLinks: true, NativeMethods.FileShareAll, out _);
        return handle is null ? null : GetFinalPath(handle);
    }
}
