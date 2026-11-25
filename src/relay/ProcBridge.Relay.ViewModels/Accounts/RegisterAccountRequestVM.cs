namespace ProcBridge.Relay.ViewModels.Accounts;

public sealed record RegisterAccountRequestVM
{
    public required string UserName { get; init; }
    public required string Password { get; init; }
}
