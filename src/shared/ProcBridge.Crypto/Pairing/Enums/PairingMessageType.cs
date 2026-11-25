namespace ProcBridge.Crypto.Pairing.Enums;

/// <summary>First byte of each pairing message.</summary>
public enum PairingMessageType : byte
{
    /// <summary>A to B: commitment to A's nonce.</summary>
    Commit = 1,

    /// <summary>B to A: B's nonce.</summary>
    Nonce = 2,

    /// <summary>A to B: A reveals its nonce.</summary>
    Reveal = 3,

    /// <summary>A to B: the user typed the correct code on A.</summary>
    Confirm = 4,
}
