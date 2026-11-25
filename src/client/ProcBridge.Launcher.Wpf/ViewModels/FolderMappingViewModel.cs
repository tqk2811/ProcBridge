using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>One editable row of the mapping list: virtual folder on machine A, folder on machine B.</summary>
public sealed class FolderMappingViewModel : ObservableObject
{
    private string _virtualRoot;
    private string _remotePath;

    public FolderMappingViewModel(FolderMapping mapping)
    {
        _virtualRoot = mapping.VirtualRoot;
        _remotePath = mapping.RemotePath;
    }

    public string VirtualRoot
    {
        get => _virtualRoot;
        set => SetProperty(ref _virtualRoot, value);
    }

    public string RemotePath
    {
        get => _remotePath;
        set => SetProperty(ref _remotePath, value);
    }

    public FolderMapping ToModel() => new() { VirtualRoot = VirtualRoot.Trim(), RemotePath = RemotePath.Trim() };
}
