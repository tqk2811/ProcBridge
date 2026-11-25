using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// One folder of machine B in the Remote Explorer tree. Lazy: the folder is listed the first time it is expanded
/// (or selected), until then it holds one placeholder child so the tree shows an expander. A refused listing
/// (PermissionDenied, OutOfScope...) is kept in <see cref="ErrorText"/> and the node has no children.
/// </summary>
public sealed class RemoteFolderNodeViewModel : ObservableObject
{
    private readonly IRemoteExplorer? _explorer;
    private readonly Action<RemoteFolderNodeViewModel>? _selected;
    private Task? _loading;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isLoading;
    private string? _errorText;

    public RemoteFolderNodeViewModel(IRemoteExplorer explorer, string name, string fullPath, Action<RemoteFolderNodeViewModel>? selected = null)
    {
        _explorer = explorer;
        _selected = selected;
        Name = name;
        FullPath = fullPath;
        Children.Add(new RemoteFolderNodeViewModel());
    }

    /// <summary>Placeholder child shown before the folder is listed.</summary>
    private RemoteFolderNodeViewModel()
    {
        Name = "Đang tải…";
        FullPath = string.Empty;
        IsPlaceholder = true;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsPlaceholder { get; }

    public ObservableCollection<RemoteFolderNodeViewModel> Children { get; } = [];

    /// <summary>Files of the folder once listed (the picker only shows folders; the Explorer tab shows these too).</summary>
    public ObservableCollection<FileEntry> Files { get; } = [];

    public bool IsLoaded { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
                _ = EnsureLoadedAsync();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value && !IsPlaceholder)
            {
                _selected?.Invoke(this);
                _ = EnsureLoadedAsync();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorText is not null;

    /// <summary>Lists the folder once; later calls return the same task. Never throws.</summary>
    public Task EnsureLoadedAsync() => IsPlaceholder ? Task.CompletedTask : _loading ??= LoadAsync();

    /// <summary>Lists the folder again (after a change on machine B or a refused listing).</summary>
    public Task ReloadAsync()
    {
        if (IsPlaceholder)
            return Task.CompletedTask;
        _loading = LoadAsync();
        return _loading;
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorText = null;
        try
        {
            DirectoryListResponse response = await _explorer!.ListDirectoryAsync(FullPath, CancellationToken.None);
            Children.Clear();
            Files.Clear();
            if (response.Status != ExplorerStatus.Ok)
            {
                ErrorText = ExplorerStatusText.Describe(response.Status, response.Error);
                return;
            }
            foreach (FileEntry entry in response.Entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.IsDirectory)
                    Children.Add(new RemoteFolderNodeViewModel(_explorer, entry.Name, entry.FullPath, _selected));
                else
                    Files.Add(entry);
            }
            IsLoaded = true;
        }
        catch (Exception e)
        {
            Children.Clear();
            ErrorText = $"Mất kết nối tới máy B: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
