using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// Folder tree of machine B (Remote Explorer): the roots machine B allows, each folder listed lazily. Used by the
/// folder picker of the profile editor and by the File tab of the Remote Explorer. Borrows the explorer.
/// </summary>
public sealed class RemoteFolderTreeViewModel : ObservableObject
{
    private readonly IRemoteExplorer _explorer;
    private RemoteFolderNodeViewModel? _selectedNode;
    private string? _errorText;
    private bool _isLoading;

    public RemoteFolderTreeViewModel(IRemoteExplorer explorer)
    {
        _explorer = explorer;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public ObservableCollection<RemoteFolderNodeViewModel> Roots { get; } = [];

    /// <summary>Drives holding an allowed root (information only).</summary>
    public ObservableCollection<DriveEntry> Drives { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public RemoteFolderNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
                OnPropertyChanged(nameof(SelectedPath));
        }
    }

    public string? SelectedPath => _selectedNode?.FullPath;

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

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Lists the roots machine B allows; a refusal lands in <see cref="ErrorText"/>. Never throws.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorText = null;
        Roots.Clear();
        Drives.Clear();
        SelectedNode = null;
        try
        {
            DrivesResponse response = await _explorer.ListDrivesAsync(CancellationToken.None);
            if (response.Status != ExplorerStatus.Ok)
            {
                ErrorText = ExplorerStatusText.Describe(response.Status, response.Error);
                return;
            }
            foreach (DriveEntry drive in response.Drives)
                Drives.Add(drive);
            foreach (string root in response.Roots)
                Roots.Add(new RemoteFolderNodeViewModel(_explorer, root, root, node => SelectedNode = node));
            if (Roots.Count == 0)
                ErrorText = "Máy B chưa cấp thư mục nào cho máy này duyệt.";
        }
        catch (Exception e)
        {
            ErrorText = $"Không kết nối được tới máy B: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
