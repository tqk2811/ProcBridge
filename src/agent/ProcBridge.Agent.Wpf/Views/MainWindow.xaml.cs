using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Services;

namespace ProcBridge.Agent.Wpf.Views;

/// <summary>
/// Main window. Only view plumbing here: the refresh timer, the tray icon and the close sequence (hide to tray, or stop
/// the server then shut the app down).
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;
    private bool _exitRequested;
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += (_, _) => _viewModel?.Refresh();
    }

    public void Attach(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTray();
        _refreshTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MinimizeToTray))
            UpdateTray();
    }

    private void UpdateTray()
    {
        if (_viewModel?.MinimizeToTray == true)
        {
            _tray ??= new TrayIcon(ShowFromTray, RequestExit);
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shuttingDown)
            return;
        if (_viewModel?.MinimizeToTray == true && !_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // Stop the server before the window goes away; the app shuts down once it stopped.
        e.Cancel = true;
        _shuttingDown = true;
        _refreshTimer.Stop();
        try
        {
            if (_viewModel is not null)
                await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ProcBridge Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _tray?.Dispose();
            _tray = null;
            Application.Current.Shutdown();
        }
    }
}
