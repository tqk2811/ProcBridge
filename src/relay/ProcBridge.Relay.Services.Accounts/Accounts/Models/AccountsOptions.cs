namespace ProcBridge.Relay.Services.Accounts.Models;

/// <summary>Configuration section <c>Relay:Accounts</c>.</summary>
public sealed class AccountsOptions
{
    public const string SectionName = "Relay:Accounts";

    /// <summary>
    /// Anyone may create an account via <c>POST api/accounts/register</c>. Off by default: accounts are created by the
    /// operator through <see cref="Seed"/>.
    /// </summary>
    public bool AllowRegistration { get; set; }

    /// <summary>
    /// Accounts created at every start. The relay keeps its data in memory only, so after a restart these (and whatever
    /// registration allows) are the only accounts. In configuration files or as environment variables
    /// <c>Relay__Accounts__Seed__0__UserName</c> / <c>Relay__Accounts__Seed__0__PasswordHash</c>.
    /// </summary>
    public List<SeedAccount> Seed { get; set; } = [];

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);
}
