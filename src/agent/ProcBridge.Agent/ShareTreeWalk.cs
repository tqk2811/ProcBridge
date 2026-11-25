using System.Text;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// State of one <see cref="Protocol.Enums.MessageType.FsTree"/> walk: breadth first over a share subtree, handed out
/// one page at a time (plan section 11: fetch the metadata of a whole tree once). Links and junctions are reported but
/// never descended into, so the walk cannot leave the share or loop. Not thread safe: the session serializes pages.
/// </summary>
internal sealed class ShareTreeWalk
{
    /// <summary>Serialized size of one directory part besides its path and entries.</summary>
    private const int DirectoryOverhead = 32;

    /// <summary>Response array headers, cursor and flags, with slack.</summary>
    private const int ResponseOverhead = 64;

    private readonly Queue<(string RelativePath, string FullPath, int Depth)> _pending = new();
    private readonly int _maxDepth;
    private int _remainingEntries;

    private string? _currentPath;
    private FileSystemInfo[] _currentEntries = [];
    private Dictionary<string, FileDetailsReader.FileDetails> _currentDetails = new();
    private int _currentIndex;
    private int _currentDepth;

    public ShareTreeWalk(string relativePath, string fullPath, int maxDepth, int maxEntries)
    {
        _maxDepth = maxDepth;
        _remainingEntries = maxEntries;
        _pending.Enqueue((relativePath, fullPath, 0));
    }

    public bool IsDone { get; private set; }

    /// <summary>The entry limit stopped the walk before the whole subtree was listed.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Next page; at least one entry when any is left, so a long name can never stall the walk.</summary>
    public FsTreeDirectory[] NextPage(int maxResponseBytes)
    {
        var page = new List<FsTreeDirectory>();
        int budget = maxResponseBytes - ResponseOverhead;
        bool anyEntry = false;
        while (!IsDone)
        {
            if (_currentPath is null && !OpenNextDirectory())
            {
                IsDone = true;
                break;
            }

            int partCost = DirectoryOverhead + Encoding.UTF8.GetMaxByteCount(_currentPath!.Length);
            if (budget - partCost < 0 && anyEntry)
                break;
            budget -= partCost;

            var part = new List<FsEntry>();
            while (_currentIndex < _currentEntries.Length)
            {
                if (_remainingEntries <= 0)
                {
                    Truncated = true;
                    IsDone = true;
                    break;
                }
                FileSystemInfo info = _currentEntries[_currentIndex];
                int cost = AgentShare.EntryCost(info.Name);
                if (budget - cost < 0 && anyEntry)
                    break;
                budget -= cost;
                part.Add(AgentShare.ToListedEntry(info, _currentDetails.TryGetValue(info.Name, out FileDetailsReader.FileDetails d) ? d : null));
                anyEntry = true;
                _remainingEntries--;
                _currentIndex++;
                if (info is DirectoryInfo && (info.Attributes & FileAttributes.ReparsePoint) == 0 && _currentDepth + 1 < _maxDepth)
                    _pending.Enqueue((Join(_currentPath, info.Name), info.FullName, _currentDepth + 1));
            }

            bool complete = _currentIndex >= _currentEntries.Length;
            page.Add(new FsTreeDirectory { Path = _currentPath, Entries = part.ToArray(), Complete = complete });
            if (!complete)
                break;
            _currentPath = null;
        }
        return page.ToArray();
    }

    /// <summary>Loads the children of the next queued directory; false when the queue is empty.</summary>
    private bool OpenNextDirectory()
    {
        while (_pending.TryDequeue(out (string RelativePath, string FullPath, int Depth) next))
        {
            try
            {
                // Same order as FsList so a cached listing looks the same whichever message filled it.
                _currentEntries = new DirectoryInfo(next.FullPath).EnumerateFileSystemInfos("*", AgentShare.ListOptions)
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                // File ids and change times of the whole directory in one scan, like FsList.
                _currentDetails = FileDetailsReader.ReadDirectory(next.FullPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Deleted or locked since it was queued: leave it out, the client lists it on demand.
                continue;
            }
            _currentPath = next.RelativePath;
            _currentDepth = next.Depth;
            _currentIndex = 0;
            return true;
        }
        return false;
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
}
