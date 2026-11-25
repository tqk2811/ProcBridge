using System.Windows.Input;

namespace ProcBridge.Agent.Wpf.Core.Mvvm;

/// <summary>
/// Command running an async body; it cannot run twice at once. Exceptions go to the error callback given at
/// construction (the ViewModel shows them), so <c>async void</c> never lets one escape. <see cref="ExecuteAsync"/> lets
/// tests await the body.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception> _onError;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception e)
        {
            _onError(e);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
