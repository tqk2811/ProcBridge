using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ProcBridge.Crypto.Identity.Interfaces;

namespace ProcBridge.Crypto.Identity;

/// <summary>DPAPI protector bound to the current Windows user (Plan section 9).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] _defaultEntropy = Encoding.ASCII.GetBytes("ProcBridge identity key v1");

    private readonly byte[] _entropy;

    public DpapiKeyProtector(byte[]? entropy = null)
    {
        _entropy = entropy ?? _defaultEntropy;
    }

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, _entropy, DataProtectionScope.CurrentUser);
}
