using System.Collections.ObjectModel;
using ProcBridge.Launcher.Models;
using ProcBridge.Launcher.Wpf.Enums;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Mvvm;
using ProcBridge.Launcher.Wpf.Services;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// One run at a time: builds the options from the profile, runs them through the shared <c>LaunchSession</c>, shows the
/// output and the state. Stop cancels the run, which kills the whole process tree (Spawn's Job Object).
/// </summary>
public sealed class RunViewModel : ObservableObject
{
    /// <summary>Oldest lines are dropped beyond this, so a chatty build does not grow the window without bound.</summary>
    public const int MaxLogLines = 5000;

    private readonly ILaunchRunner _runner;
    private readonly LaunchOptionsFactory _optionsFactory;
    private readonly SynchronizationContext? _uiContext;
    private CancellationTokenSource? _cancel;
    private RunState _state = RunState.Idle;
    private string _statusText = "Chưa chạy.";

    public RunViewModel(ILaunchRunner runner, LaunchOptionsFactory optionsFactory)
    {
        _runner = runner;
        _optionsFactory = optionsFactory;
        _uiContext = SynchronizationContext.Current;
        StopCommand = new RelayCommand(Stop, () => State == RunState.Running);
        ClearLogCommand = new RelayCommand(Log.Clear);
    }

    public ObservableCollection<string> Log { get; } = [];

    public RelayCommand StopCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public RunState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRunning => _state is RunState.Running or RunState.Stopping;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Runs <paramref name="profile"/>; returns when the whole tree has exited. Never throws.</summary>
    public async Task RunAsync(LaunchProfile profile, PairedMachine? machine)
    {
        if (IsRunning)
            return;
        LauncherOptions options;
        try
        {
            options = _optionsFactory.Create(profile, machine);
        }
        catch (ProfileValidationException e)
        {
            State = RunState.Failed;
            StatusText = "Hồ sơ chưa chạy được: " + string.Join(" ", e.Errors);
            return;
        }

        Log.Clear();
        AppendLine($"> {options.CommandLine}");
        _cancel = new CancellationTokenSource();
        State = RunState.Running;
        StatusText = $"Đang chạy {profile.Name} trên máy B {machine?.Name}…";
        try
        {
            int exitCode = await _runner.RunAsync(options, OnOutputLine, _cancel.Token);
            State = RunState.Exited;
            StatusText = $"Đã thoát, mã {exitCode}.";
        }
        catch (OperationCanceledException)
        {
            State = RunState.Stopped;
            StatusText = "Đã dừng: cả cây process đã bị kill.";
        }
        catch (Exception e)
        {
            State = RunState.Failed;
            StatusText = $"Lỗi khi chạy: {e.Message}";
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
        }
    }

    public void Stop()
    {
        if (State != RunState.Running || _cancel is null)
            return;
        State = RunState.Stopping;
        StatusText = "Đang dừng cả cây process…";
        _cancel.Cancel();
    }

    private void OnOutputLine(string line)
    {
        if (_uiContext is null)
            AppendLine(line);
        else
            _uiContext.Post(_ => AppendLine(line), null);
    }

    private void AppendLine(string line)
    {
        lock (Log)
        {
            Log.Add(line);
            while (Log.Count > MaxLogLines)
                Log.RemoveAt(0);
        }
    }
}
