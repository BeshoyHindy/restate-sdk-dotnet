// Verification entry point for the vendored Chaos.NaCl Ed25519 ref10 port
// (https://github.com/CodesInChaos/Chaos.NaCl — public domain / CC0).
// Adapted from Ed25519Operations.crypto_sign_verify: span-based, hashes with
// the BCL SHA-512, and additionally enforces the RFC 8032 requirement s < L.

using System.Buffers;
using System.Security.Cryptography;

namespace Restate.Sdk.Internal.Identity.Ed25519;

/// <summary>
///     Verify-only Ed25519 (RFC 8032). Signing is intentionally not included —
///     the SDK only ever validates signatures produced by the Restate server.
/// </summary>
internal static class Ed25519
{
    /// <summary>Size of an Ed25519 public key in bytes.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Size of an Ed25519 signature in bytes.</summary>
    public const int SignatureSize = 64;

    /// <summary>
    ///     The group order L = 2^252 + 27742317777372353535851937790883648493, little-endian.
    ///     Used to reject non-canonical (malleable) signatures, per RFC 8032 §5.1.7.
    /// </summary>
    private static ReadOnlySpan<byte> GroupOrder =>
    [
        0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58,
        0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10,
    ];

    /// <summary>
    ///     Verifies an Ed25519 signature over <paramref name="message" />.
    /// </summary>
    /// <param name="signature">The 64-byte signature (R || s).</param>
    /// <param name="message">The signed message bytes.</param>
    /// <param name="publicKey">The 32-byte Ed25519 public key.</param>
    /// <returns><see langword="true" /> if the signature is valid; otherwise <see langword="false" />.</returns>
    public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != SignatureSize || publicKey.Length != PublicKeySize)
        {
            return false;
        }

        // Reject s >= L (non-canonical scalar; implies the original (sig[63] & 224) != 0 check).
        if (!IsCanonicalScalar(signature.Slice(32, 32)))
        {
            return false;
        }

        if (GroupOperations.ge_frombytes_negate_vartime(out GroupElementP3 negA, publicKey, 0) != 0)
        {
            return false;
        }

        // h = SHA512(R || publicKey || message), then reduced mod L.
        Span<byte> h = stackalloc byte[64];
        int hashInputLength = 64 + message.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(hashInputLength);
        try
        {
            signature[..32].CopyTo(rented);
            publicKey.CopyTo(rented.AsSpan(32));
            message.CopyTo(rented.AsSpan(64));
            SHA512.HashData(rented.AsSpan(0, hashInputLength), h);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        ScalarOperations.sc_reduce(h);

        // checkR = h * (-A) + s * B; valid iff checkR == R.
        GroupOperations.ge_double_scalarmult_vartime(out GroupElementP2 r, h[..32], ref negA, signature.Slice(32, 32));
        Span<byte> checkR = stackalloc byte[32];
        GroupOperations.ge_tobytes(checkR, 0, ref r);
        return CryptographicOperations.FixedTimeEquals(checkR, signature[..32]);
    }

    /// <summary>Returns <see langword="true" /> if the little-endian scalar <paramref name="s" /> is &lt; L.</summary>
    private static bool IsCanonicalScalar(ReadOnlySpan<byte> s)
    {
        ReadOnlySpan<byte> order = GroupOrder;
        for (int i = 31; i >= 0; i--)
        {
            if (s[i] < order[i])
            {
                return true;
            }

            if (s[i] > order[i])
            {
                return false;
            }
        }

        // s == L is not < L.
        return false;
    }
}
