namespace ProcBridge.Crypto.Identity.Interfaces;

/// <summary>Protects key material at rest (DPAPI on Windows, a fake in tests).</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] plaintext);

    /// <exception cref="System.Security.Cryptography.CryptographicException">Blob cannot be unprotected.</exception>
    byte[] Unprotect(byte[] protectedData);
}
