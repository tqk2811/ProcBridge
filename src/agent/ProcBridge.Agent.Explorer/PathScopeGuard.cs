using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Native;

namespace ProcBridge.Agent.Explorer;

/// <summary>
/// Decides whether a path sent by machine A lies inside the folders the policy allows.
/// Two checks: a lexical one on the normalized path (stops <c>..</c>, prefix tricks, UNC/device
/// paths, alternate data streams) and a physical one on the final path Windows resolves for the
/// deepest existing part of the path (stops junctions, symbolic links and mount points that lead
/// outside every root).
/// </summary>
/// <remarks>
/// The physical check runs before the operation, so a link swapped in between check and use
/// (TOCTOU) is not covered yet; see docs/Backlog-vi.md.
/// </remarks>
internal sealed class PathScopeGuard
{
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
            physical.Add(Directory.Exists(full) ? GetFinalPath(full) ?? full : full);
        }

        _roots = [.. lexical];
        _finalRoots = [.. physical];
    }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Resolves a path for reading: the path itself (and the link targets on it) must stay inside a root.</summary>
    public string ResolveForRead(string? path)
    {
        var full = Normalize(path);
        CheckPhysical(full);
        return full;
    }

    /// <summary>
    /// Resolves a path whose entry itself is changed (delete, rename source, create). Only the
    /// parent is resolved physically, so deleting a link removes the link, never its target.
    /// A root itself cannot be changed.
    /// </summary>
    public string ResolveForChange(string? path)
    {
        var full = Normalize(path);
        if (_roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "An allowed root itself cannot be changed.");
        var parent = Path.GetDirectoryName(full)
            ?? throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "A drive root cannot be changed.");
        CheckPhysical(parent);
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

    private void CheckPhysical(string full)
    {
        // Walk up to the deepest part that exists, resolve it, then re-append the missing tail.
        var existing = full;
        var tail = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
                return;
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var final = GetFinalPath(existing)
            ?? throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "Cannot resolve the real location of the path.");
        while (tail.Count > 0)
            final = Path.Combine(final, tail.Pop());
        if (!_finalRoots.Any(r => IsUnder(final, r)))
            throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "Path leads outside the allowed folders through a link.");
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

    /// <summary>Final path of an existing file or directory with every link resolved, without the <c>\\?\</c> prefix.</summary>
    private static unsafe string? GetFinalPath(string path)
    {
        using var handle = NativeMethods.CreateFile(path, NativeMethods.FileReadAttributes, NativeMethods.FileShareAll, 0,
            NativeMethods.OpenExisting, NativeMethods.FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
            return null;

        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* p = buffer)
                length = NativeMethods.GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
            if (length == 0)
                return null;
            if (length < buffer.Length)
            {
                var result = new string(buffer, 0, (int)length);
                if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    return @"\\" + result[8..];
                if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return TrimEnd(result[4..]);
                return TrimEnd(result);
            }

            buffer = new char[length];
        }
    }
}
