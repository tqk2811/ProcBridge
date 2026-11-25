using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProcBridge.Agent.Wpf.Core.Mvvm;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base for the ViewModels.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Sets <paramref name="field"/> and raises <see cref="PropertyChanged"/> when the value changed.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
