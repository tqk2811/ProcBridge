using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Wpf.Core.Mvvm;

namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>One check box of the Explorer profile editor.</summary>
public sealed class PermissionOptionViewModel(ExplorerPermissions flag, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    public ExplorerPermissions Flag { get; } = flag;

    public string Name => Flag.ToString();

    public string Label { get; } = ExplorerPermissionLabels.Describe(flag);

    public bool IsWrite { get; } = ExplorerPermissionLabels.IsWrite(flag);

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}
