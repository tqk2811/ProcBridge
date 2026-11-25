using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Services.Common.Security;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.ViewModels.Accounts;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>Sign-in must cost the same for a user name that exists and one that does not (no user enumeration by timing).</summary>
[TestClass]
public sealed class AccountTimingTests
{
    [TestMethod]
    public async Task ĐăngNhậpTênKhôngTồnTạiVẫnTốnMộtLầnXácMinhMậtKhẩu()
    {
        var hasher = new CountingPasswordHasher();
        await using var factory = new RelayApiFactory();
        await using var custom = new CustomFactory(factory, hasher);
        var api = new RelayApiImplement(custom.CreateClient());

        var ex = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => api.Accounts.LoginAsync(new LoginRequestVM { UserName = "khong-ton-tai", Password = "mat-khau-bat-ky" }));
        Assert.AreEqual(401, ex.StatusCode);
        Assert.AreEqual(1, hasher.VerifyCalls, "Tên không tồn tại phải verify với hash giả, cùng chi phí với tên có thật.");
    }

    private sealed class CountingPasswordHasher : PasswordHasher
    {
        private int _verifyCalls;

        public CountingPasswordHasher() : base(1000)
        {
        }

        public int VerifyCalls => Volatile.Read(ref _verifyCalls);

        public override bool Verify(string password, string stored)
        {
            Interlocked.Increment(ref _verifyCalls);
            return base.Verify(password, stored);
        }
    }

    /// <summary>The same in-process relay with the password hasher swapped for a counting one.</summary>
    private sealed class CustomFactory : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _inner;

        public CustomFactory(RelayApiFactory factory, PasswordHasher hasher)
            => _inner = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<PasswordHasher>();
                s.AddSingleton(hasher);
            }));

        public HttpClient CreateClient() => _inner.CreateClient();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
