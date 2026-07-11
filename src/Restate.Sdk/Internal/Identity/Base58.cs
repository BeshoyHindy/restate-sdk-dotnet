namespace Restate.Sdk.Internal.Identity;

/// <summary>
///     Base58 (Bitcoin alphabet) decoder. Decode-only: the SDK only parses
///     <c>publickeyv1_</c> identity keys, it never emits base58.
/// </summary>
internal static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>
    ///     Decodes a base58btc string. Only called at configuration time, so the
    ///     big-integer-style O(n²) algorithm is acceptable.
    /// </summary>
    /// <param name="value">The base58 input.</param>
    /// <returns>The decoded bytes, or <see langword="null" /> if the input contains invalid characters.</returns>
    public static byte[]? Decode(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return [];
        }

        // Leading '1's encode leading zero bytes.
        int leadingZeros = 0;
        while (leadingZeros < value.Length && value[leadingZeros] == '1')
        {
            leadingZeros++;
        }

        // Upper bound on decoded size: log(58) / log(256) ≈ 0.733.
        Span<byte> buffer = value.Length <= 256 ? stackalloc byte[188] : new byte[(value.Length * 733 / 1000) + 1];
        int length = 0;

        for (int i = leadingZeros; i < value.Length; i++)
        {
            int digit = Alphabet.IndexOf(value[i], StringComparison.Ordinal);
            if (digit < 0)
            {
                return null;
            }

            int carry = digit;
            for (int j = 0; j < length; j++)
            {
                carry += buffer[j] * 58;
                buffer[j] = (byte)carry;
                carry >>= 8;
            }

            while (carry > 0)
            {
                buffer[length++] = (byte)carry;
                carry >>= 8;
            }
        }

        // buffer holds the value little-endian; emit big-endian with the leading zeros restored.
        byte[] result = new byte[leadingZeros + length];
        for (int i = 0; i < length; i++)
        {
            result[leadingZeros + i] = buffer[length - 1 - i];
        }

        return result;
    }
}
