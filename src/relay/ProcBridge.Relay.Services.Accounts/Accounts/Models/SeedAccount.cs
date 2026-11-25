namespace ProcBridge.Relay.Services.Accounts.Models;

/// <summary>One account created when the relay starts (<c>Relay:Accounts:Seed</c>).</summary>
public sealed class SeedAccount
{
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>, made by <c>ProcBridge.Relay.Api account hash</c>.
    /// Never the plain password.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;
}
