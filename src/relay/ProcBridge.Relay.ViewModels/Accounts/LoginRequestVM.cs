namespace ProcBridge.Relay.ViewModels.Accounts;

public sealed record LoginRequestVM
{
    public required string UserName { get; init; }
    public required string Password { get; init; }
}
