using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Agent.Wpf.Core.Mvvm;

namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>One row of the share grid (edited in place).</summary>
public sealed class ShareItemViewModel : ObservableObject
{
    private string _name;
    private string _path;
    private bool _allowWrite;

    public ShareItemViewModel(ShareSettings settings)
    {
        _name = settings.Name;
        _path = settings.Path;
        _allowWrite = settings.AllowWrite;
    }

    /// <summary>Raised when any field changed.</summary>
    public event Action? Changed;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                Changed?.Invoke();
        }
    }

    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value))
                Changed?.Invoke();
        }
    }

    /// <summary>Saved only until the server serves writes (M3).</summary>
    public bool AllowWrite
    {
        get => _allowWrite;
        set
        {
            if (SetProperty(ref _allowWrite, value))
                Changed?.Invoke();
        }
    }

    public ShareSettings ToSettings() => new() { Name = Name.Trim(), Path = Path.Trim(), AllowWrite = AllowWrite };
}
