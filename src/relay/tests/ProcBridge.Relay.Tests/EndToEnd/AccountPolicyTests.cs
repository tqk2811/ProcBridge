using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>Self-registration switch, account seeding, the operator hash command, restart behaviour and the sign-in rate limit, each on its own in-process relay.</summary>
[TestClass]
[DoNotParallelize] // The hash command reads the password from a process-wide environment variable.
public sealed class AccountPolicyTests
{
    [TestMethod]
    public async Task MặcĐịnhKhôngChoTựĐăngKý()
    {
        Assert.IsFalse(new Services.Accounts.Models.AccountsOptions().AllowRegistration);
        // The test factory turns registration on; put back what the shipped appsettings.json says.
        await using var factory = new RelayApiFactory(new Dictionary<string, string?> { ["Relay:Accounts:AllowRegistration"] = "false" });
        var api = new RelayApiImplement(factory.CreateClient());

        var ex = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => api.Accounts.RegisterAsync(new RegisterAccountRequestVM { UserName = "ai-do", Password = "matkhau-dai-du" }));
        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual("REGISTRATION_DISABLED", ex.Code);
    }

    [TestMethod]
    public async Task LệnhHashInHashDùngĐượcChoSeedKhiTắtĐăngKý()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Environment.SetEnvironmentVariable(OperatorCommands.PasswordEnvironmentVariable, "matkhau-dai-du");
        try
        {
            var code = await OperatorCommands.RunAsync(["account", "hash"], output, error);
            Assert.AreEqual(0, code, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperatorCommands.PasswordEnvironmentVariable, null);
        }
        var hash = output.ToString().Trim();
        StringAssert.StartsWith(hash, "pbkdf2-sha256$");
        Assert.DoesNotContain("matkhau-dai-du", output.ToString());
        Assert.AreEqual(2, await OperatorCommands.RunAsync(["account", "create", "x"], output, error));

        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:Accounts:AllowRegistration"] = "false",
            ["Relay:Accounts:Seed:0:UserName"] = "Quan-Tri",
            ["Relay:Accounts:Seed:0:PasswordHash"] = hash,
        });
        var api = new RelayApiImplement(factory.CreateClient());
        var login = await api.Accounts.LoginAsync(new LoginRequestVM { UserName = "quan-tri", Password = "matkhau-dai-du" });
        Assert.AreEqual("quan-tri", login.Account.UserName);
    }

    [TestMethod]
    public async Task SeedSaiĐịnhDạngLàmRelayKhôngKhởiĐộng()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:Accounts:Seed:0:UserName"] = "quan-tri",
            ["Relay:Accounts:Seed:0:PasswordHash"] = "matkhau-dai-du",
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [TestMethod]
    public async Task DữLiệuMấtKhiRelayKhởiĐộngLại()
    {
        var hash = new Services.Common.Security.PasswordHasher(1000).Hash("matkhau-dai-du");
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Accounts:Seed:0:UserName"] = "quan-tri",
            ["Relay:Accounts:Seed:0:PasswordHash"] = hash,
        };
        await using (var first = new RelayApiFactory(settings))
        {
            var api = new RelayApiImplement(first.CreateClient());
            var token = (await api.Accounts.LoginAsync(new LoginRequestVM { UserName = "quan-tri", Password = "matkhau-dai-du" })).Token;
            api.SetBearerToken(token);
            await api.Devices.RegisterAsync(new ViewModels.Devices.RegisterDeviceRequestVM { Name = "may-1" });
            Assert.HasCount(1, await api.Devices.GetAllAsync());
        }

        // "Restart": a new host. Only the seeded account is back; the device and the old session token are gone.
        await using var second = new RelayApiFactory(settings);
        var again = new RelayApiImplement(second.CreateClient());
        var login = await again.Accounts.LoginAsync(new LoginRequestVM { UserName = "quan-tri", Password = "matkhau-dai-du" });
        again.SetBearerToken(login.Token);
        Assert.IsEmpty(await again.Devices.GetAllAsync());
    }

    [TestMethod]
    public async Task ĐăngNhậpSaiNhiềuLầnBịChặn429()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
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
