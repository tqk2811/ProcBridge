using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// One tab of the Remote Explorer. Reads are skipped (with a clear message) when the profile of machine B lacks the read
/// permission; write commands are exposed only with their own permission and always ask for confirmation. The Agent
/// still checks everything, so a refusal coming back is shown too. Borrows the explorer.
/// </summary>
public abstract class ExplorerPageViewModel : ObservableObject
{
    private string? _errorText;
    private string? _statusText;
    private bool _isLoading;

    protected ExplorerPageViewModel(IRemoteExplorer explorer, ExplorerPermissions granted, IUserDialogs dialogs, ExplorerPermissions readPermission)
    {
        Explorer = explorer;
        Granted = granted;
        Dialogs = dialogs;
        ReadPermission = readPermission;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool CanRead => Granted.HasFlag(ReadPermission);

    public string? ErrorText
    {
        get => _errorText;
        protected set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorText is not null;

    /// <summary>Result of the last write operation.</summary>
    public string? StatusText
    {
        get => _statusText;
        protected set => SetProperty(ref _statusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    protected IRemoteExplorer Explorer { get; }

    protected ExplorerPermissions Granted { get; }

    protected IUserDialogs Dialogs { get; }

    private ExplorerPermissions ReadPermission { get; }

    /// <summary>Loads the tab; never throws.</summary>
    public async Task LoadAsync()
    {
        if (!CanRead)
        {
            ErrorText = ExplorerStatusText.NotGranted(ReadPermission);
            return;
        }
        IsLoading = true;
        ErrorText = null;
        try
        {
            ExplorerResponse response = await LoadCoreAsync();
            if (response.Status != ExplorerStatus.Ok)
                ErrorText = ExplorerStatusText.Describe(response.Status, response.Error);
        }
        catch (Exception e)
        {
            ErrorText = $"Mất kết nối tới máy B: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Lists the content; the derived class fills its collections only when the status is Ok.</summary>
    protected abstract Task<ExplorerResponse> LoadCoreAsync();

    /// <summary>Asks, runs one write operation, shows its outcome and reloads after a success. Never throws.</summary>
    protected async Task ConfirmAndRunAsync(string title, string question, Func<Task<ExplorerAckResponse>> operation)
    {
        if (!Dialogs.Confirm(title, question))
            return;
        try
        {
            ExplorerAckResponse ack = await operation();
            StatusText = ack.Status == ExplorerStatus.Ok ? $"{title}: xong." : $"{title}: {ExplorerStatusText.Describe(ack.Status, ack.Error)}";
            if (ack.Status == ExplorerStatus.Ok)
                await LoadAsync();
        }
        catch (Exception e)
        {
            StatusText = $"{title}: mất kết nối tới máy B: {e.Message}";
        }
    }
}
