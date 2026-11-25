using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>Self-registration switch, the operator command and the sign-in rate limit, each on its own in-process relay (in-memory repositories).</summary>
[TestClass]
[DoNotParallelize] // The operator command reads the password from a process-wide environment variable.
public sealed class AccountPolicyTests
{
    [TestMethod]
    public async Task MặcĐịnhKhôngChoTựĐăngKý()
    {
        Assert.IsFalse(new Services.Accounts.Models.AccountsOptions().AllowRegistration);
        // The test factory turns registration on; put back what the shipped appsettings.json says.
        await using var factory = new RelayApiFactory(null, new Dictionary<string, string?> { ["Relay:Accounts:AllowRegistration"] = "false" });
        var api = new RelayApiImplement(factory.CreateClient());

        var ex = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => api.Accounts.RegisterAsync(new RegisterAccountRequestVM { UserName = "ai-do", Password = "matkhau-dai-du" }));
        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual("REGISTRATION_DISABLED", ex.Code);
    }

    [TestMethod]
    public async Task LệnhVậnHànhTạoĐượcTàiKhoảnKhiTắtĐăngKý()
    {
        await using var factory = new RelayApiFactory(null, new Dictionary<string, string?> { ["Relay:Accounts:AllowRegistration"] = "false" });
        var output = new StringWriter();
        var error = new StringWriter();
        Environment.SetEnvironmentVariable(OperatorCommands.PasswordEnvironmentVariable, "matkhau-dai-du");
        try
        {
            var code = await OperatorCommands.RunAsync(factory.Services, ["account", "create", "quan-tri"], output, error);
            Assert.AreEqual(0, code, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorCommands.PasswordEnvironmentVariable, null);
        }

        var api = new RelayApiImplement(factory.CreateClient());
        var login = await api.Accounts.LoginAsync(new LoginRequestVM { UserName = "quan-tri", Password = "matkhau-dai-du" });
        Assert.AreEqual("quan-tri", login.Account.UserName);
        Assert.AreEqual(2, await OperatorCommands.RunAsync(factory.Services, ["account", "delete", "x"], output, error));
    }

    [TestMethod]
    public async Task ĐăngNhậpSaiNhiềuLầnBịChặn429()
    {
        await using var factory = new RelayApiFactory(null, new Dictionary<string, string?>
        {
            ["Relay:RateLimit:AuthPermitLimit"] = "3",
            ["Relay:RateLimit:AuthWindow"] = "00:10:00",
        });
        var api = new RelayApiImplement(factory.CreateClient());
        var wrong = new LoginRequestVM { UserName = "khong-co", Password = "sai-mat-khau" };

        for (var i = 0; i < 3; i++)
            Assert.AreEqual(401, (await Assert.ThrowsExactlyAsync<RelayApiException>(() => api.Accounts.LoginAsync(wrong))).StatusCode);
        var blocked = await Assert.ThrowsExactlyAsync<RelayApiException>(() => api.Accounts.LoginAsync(wrong));
        Assert.AreEqual(429, blocked.StatusCode);
        Assert.AreEqual("TOO_MANY_REQUESTS", blocked.Code);
        // Register shares the same budget.
        var register = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => api.Accounts.RegisterAsync(new RegisterAccountRequestVM { UserName = "moi", Password = "matkhau-dai-du" }));
        Assert.AreEqual(429, register.StatusCode);
    }
}
