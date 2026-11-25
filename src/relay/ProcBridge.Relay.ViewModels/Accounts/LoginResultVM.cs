namespace ProcBridge.Relay.ViewModels.Accounts;

/// <param name="Token">Account session token (bearer). Shown once; the relay only keeps its hash.</param>
public sealed record LoginResultVM(string Token, DateTime ExpiresAt, AccountVM Account);
