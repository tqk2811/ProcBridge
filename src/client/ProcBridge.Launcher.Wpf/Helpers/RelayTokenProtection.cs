using System.Text;
using ProcBridge.Crypto.Identity;

namespace ProcBridge.Launcher.Wpf.Helpers;

/// <summary>Keeps the relay device token of a profile DPAPI-protected (current user) in the profile file, never in clear.</summary>
public static class RelayTokenProtection
{
    public static string? Protect(string token)
        => token.Length == 0 ? null : Convert.ToBase64String(new DpapiKeyProtector().Protect(Encoding.UTF8.GetBytes(token)));

    /// <summary>The token, or empty when there is none or it cannot be read (other user, other machine).</summary>
    public static string Unprotect(string? protectedToken)
    {
        if (string.IsNullOrEmpty(protectedToken))
            return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(new DpapiKeyProtector().Unprotect(Convert.FromBase64String(protectedToken)));
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }
}
