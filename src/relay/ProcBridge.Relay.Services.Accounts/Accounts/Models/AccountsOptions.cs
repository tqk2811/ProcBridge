namespace ProcBridge.Relay.Services.Accounts.Models;

/// <summary>Configuration section <c>Relay:Accounts</c>.</summary>
public sealed class AccountsOptions
{
    public const string SectionName = "Relay:Accounts";

    /// <summary>
    /// Anyone may create an account via <c>POST api/accounts/register</c>. Off by default: accounts are created by the
    /// operator with <c>ProcBridge.Relay.Api account create &lt;user&gt;</c> on the relay host.
    /// </summary>
    public bool AllowRegistration { get; set; }

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);
}
