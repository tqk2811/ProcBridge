using System.Security.Cryptography;
using System.Text;

namespace ProcBridge.Relay.Shared.Tunnel;

/// <summary>
/// Pairing invite code of the relay (plan 9, "ghép cặp qua relay"): machine B shows it, the user types it on machine A,
/// and both derive the same lookup key from it, under which B opened a pairing slot on the relay. The relay only sees
/// the lookup key. It is NOT a secret against the relay (8 digits are quick to brute force from the hash): it only finds
/// machine B. The pairing that follows still runs the Noise XX handshake and the 6-digit code end to end, and both users
/// compare fingerprints, so a relay that routes to another machine is caught there.
/// </summary>
public static class RelayPairingCode
{
    /// <summary>Digits of an invite code; deliberately not 6, so it is not confused with the 6-digit pairing code.</summary>
    public const int Length = 8;

    /// <summary>Length of a lookup key: SHA-256 in lowercase hex.</summary>
    public const int LookupKeyLength = 64;

    private const string Label = "ProcBridge relay pairing v1\n";

    /// <summary>A fresh random code of <see cref="Length"/> digits (no separator).</summary>
    public static string Generate()
    {
        Span<char> digits = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(digits);
    }

    /// <summary>The code as shown to users: <c>1234-5678</c>.</summary>
    public static string Format(string code) => code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>Accepts what a user typed (spaces and dashes ignored); false unless exactly <see cref="Length"/> digits remain.</summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = string.Empty;
        if (input is null)
            return false;
        var digits = new StringBuilder(Length);
        foreach (char c in input)
        {
            if (c is ' ' or '-' or '\t')
                continue;
            if (!char.IsAsciiDigit(c))
                return false;
            digits.Append(c);
        }
        if (digits.Length != Length)
            return false;
        code = digits.ToString();
        return true;
    }

    /// <summary>Lookup key of a normalized code: lowercase hex of SHA-256(label | code).</summary>
    public static string DeriveLookupKey(string code)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Label + code)));

    /// <summary>True for something shaped like a lookup key (64 lowercase hex digits); the relay refuses anything else.</summary>
    public static bool IsLookupKey(string? value)
        => value is { Length: LookupKeyLength } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
