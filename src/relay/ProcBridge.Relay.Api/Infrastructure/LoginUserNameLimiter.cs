using System.Threading.RateLimiting;

using ProcBridge.Relay.Services.Common.Common.Errors;

namespace ProcBridge.Relay.Api.Infrastructure;

/// <summary>
/// Fixed window per (normalized) user name on login, next to the per-address limit of <see cref="AuthRateLimitOptions"/>.
/// The address alone is weak when many clients share one (NAT) or when the relay cannot see the real one behind a
/// proxy. Counts attempts for names that do not exist too, so it does not reveal which names do.
/// </summary>
public sealed class LoginUserNameLimiter : IDisposable
{
    private const int MaxKeyLength = 64;

    private readonly PartitionedRateLimiter<string> _limiter;

    public LoginUserNameLimiter(AuthRateLimitOptions options)
    {
        _limiter = PartitionedRateLimiter.Create<string, string>(userName => RateLimitPartition.GetFixedWindowLimiter(
            userName,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.LoginUserNamePermitLimit,
                Window = options.AuthWindow,
                QueueLimit = 0,
            }));
    }

    /// <summary>Counts one attempt; throws the 429 envelope error when the user name is over its budget.</summary>
    public void Acquire(string? userName)
    {
        var key = (userName ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length > MaxKeyLength)
            key = key[..MaxKeyLength];
        using var lease = _limiter.AttemptAcquire(key);
        if (!lease.IsAcquired)
            throw ApiErrorException.TooManyRequests(RelayErrors.TooManyRequests, "Thử quá nhiều lần, hãy đợi rồi thử lại.");
    }

    public void Dispose() => _limiter.Dispose();
}
