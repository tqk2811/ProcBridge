namespace ProcBridge.Launcher.Wpf.Services;

/// <summary>The profile cannot be run; <see cref="Errors"/> lists what to fix, in user language.</summary>
public sealed class ProfileValidationException : Exception
{
    public ProfileValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
