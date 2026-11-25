using System.Security.Cryptography;
using System.Text;

namespace ProcBridge.Relay.Services.Common.Security;

/// <summary>Random bearer secrets and their stored form (SHA-256 hex). Pure functions, hence static.</summary>
public static class SecretTokens
{
    /// <summary>A new 256-bit random token, base64url, with <paramref name="prefix"/> in front.</summary>
    public static string Create(string prefix)
        => prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 hex of the token: what the database stores and looks up.</summary>
    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Constant-time comparison of two secrets.</summary>
    public static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
