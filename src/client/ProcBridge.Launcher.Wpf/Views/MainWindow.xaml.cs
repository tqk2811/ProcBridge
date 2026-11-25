using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ProcBridge.Launcher.Wpf.ViewModels;

namespace ProcBridge.Launcher.Wpf.Views;

public partial class MainWindow : Window
{
    private bool _closeAfterRun;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
                main.Run.Log.CollectionChanged += OnLogChanged;
        };
    }

    /// <summary>Keeps the newest output line in view.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    /// <summary>A running tree is stopped (killed) before the window closes, so nothing outlives the Launcher.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel { Run: { IsRunning: true } run })
            return;
        e.Cancel = true;
        if (_closeAfterRun)
            return;
        if (MessageBox.Show(this, "Đang có lần chạy. Dừng (kill cả cây process) rồi thoát?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _closeAfterRun = true;
        run.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RunViewModel.IsRunning) && !run.IsRunning)
                Dispatcher.BeginInvoke(Close);
        };
        run.Stop();
    }

    private void OnSubKeyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SubKeyList.SelectedItem is string subKey && SubKeyList.DataContext is RegistryViewModel registry)
            registry.OpenSubKeyCommand.Execute(subKey);
    }
}
