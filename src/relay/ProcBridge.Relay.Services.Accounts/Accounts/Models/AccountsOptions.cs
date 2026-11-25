namespace ProcBridge.Relay.Services.Accounts.Models;

/// <summary>Configuration section <c>Relay:Accounts</c>.</summary>
public sealed class AccountsOptions
{
    public const string SectionName = "Relay:Accounts";

    /// <summary>Anyone may create an account via <c>POST api/accounts/register</c>.</summary>
    public bool AllowRegistration { get; set; } = true;

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);
}
